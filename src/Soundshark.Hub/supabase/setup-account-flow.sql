-- Run once in the project's SQL editor before deploying the updated function.
-- No usernames, emails, or recovery records are exposed to client roles.
begin;
create schema if not exists hub_private;
revoke all on schema hub_private from public, anon, authenticated;

create table hub_private.accounts (
  user_id uuid primary key references auth.users(id) on delete cascade,
  username text unique check (username ~ '^[a-z0-9_]{3,24}$'),
  email text not null unique,
  last_recovery_at timestamptz
);
alter table hub_private.accounts enable row level security;
revoke all on hub_private.accounts from public, anon, authenticated;

-- Existing unique, valid display names become usernames. Duplicate or invalid
-- legacy names retain email login; do not guess which account owns a name.
with candidates as (
  select id, lower(email) as email,
    lower(coalesce(raw_user_meta_data->>'username',raw_user_meta_data->>'display_name')) as name
  from auth.users where email is not null
), counted as (
  select *, count(*) over(partition by name) as matches from candidates
)
insert into hub_private.accounts(user_id,email,username)
select id,email,case when name ~ '^[a-z0-9_]{3,24}$' and matches=1 then name else null end from counted;

create function hub_private.sync_account()
returns trigger language plpgsql security definer set search_path='' as $$
declare chosen text;
begin
  if new.email is null then return new; end if;
  chosen := lower(new.raw_user_meta_data->>'username');
  if chosen is not null and chosen !~ '^[a-z0-9_]{3,24}$' then
    raise exception 'Username must be 3-24 letters, numbers or underscores';
  end if;
  insert into hub_private.accounts(user_id,email,username)
  values(new.id,lower(new.email),chosen)
  on conflict(user_id) do update set email=excluded.email,
    username=coalesce(excluded.username,hub_private.accounts.username);
  return new;
end $$;
revoke all on function hub_private.sync_account() from public,anon,authenticated;
create trigger hub_sync_account after insert or update of email,raw_user_meta_data on auth.users
for each row execute function hub_private.sync_account();

create table hub_private.recovery (
  id uuid primary key default gen_random_uuid(),
  user_id uuid not null references hub_private.accounts(user_id) on delete cascade,
  attempts integer not null default 0 check(attempts between 0 and 3),
  sent_at timestamptz not null default now(),
  expires_at timestamptz not null default now()+interval '15 minutes',
  consumed boolean not null default false
);
alter table hub_private.recovery enable row level security;
revoke all on hub_private.recovery from public,anon,authenticated;
create index hub_recovery_user on hub_private.recovery(user_id);
create index hub_recovery_expiry on hub_private.recovery(expires_at);

create table hub_private.auth_limits (
  key text primary key, started_at timestamptz not null, attempts integer not null
);
alter table hub_private.auth_limits enable row level security;
revoke all on hub_private.auth_limits from public,anon,authenticated;

create function public.hub_auth_throttle(p_key text)
returns boolean language plpgsql security definer set search_path='' as $$
declare accepted text;
begin
  -- All auth traffic also shares a fixed global ceiling, preventing unbounded
  -- limiter growth from random identifiers. Atomic counters work across workers.
  insert into hub_private.auth_limits values('global',now(),1)
  on conflict(key) do update set
    started_at=case when hub_private.auth_limits.started_at < now()-interval '1 minute' then now() else hub_private.auth_limits.started_at end,
    attempts=case when hub_private.auth_limits.started_at < now()-interval '1 minute' then 1 else hub_private.auth_limits.attempts+1 end
  where hub_private.auth_limits.started_at < now()-interval '1 minute' or hub_private.auth_limits.attempts<300
  returning key into accepted;
  if accepted is null then return false; end if;
  accepted := null;
  insert into hub_private.auth_limits values(md5(left(lower(p_key),300)),now(),1)
  on conflict(key) do update set
    started_at=case when hub_private.auth_limits.started_at < now()-interval '1 minute' then now() else hub_private.auth_limits.started_at end,
    attempts=case when hub_private.auth_limits.started_at < now()-interval '1 minute' then 1 else hub_private.auth_limits.attempts+1 end
  where hub_private.auth_limits.started_at < now()-interval '1 minute' or hub_private.auth_limits.attempts<20
  returning key into accepted;
  delete from hub_private.auth_limits where started_at<now()-interval '1 day';
  return accepted is not null;
end $$;

create function public.hub_auth_lookup(p_identifier text)
returns jsonb language sql stable security definer set search_path='' as $$
  select jsonb_build_object('email',email) from hub_private.accounts
  where case when strpos(p_identifier,'@')>0 then email=lower(trim(p_identifier))
    else username=lower(trim(p_identifier)) end limit 1
$$;

create function public.hub_recovery_begin(p_identifier text)
returns jsonb language plpgsql security definer set search_path='' as $$
declare account hub_private.accounts; challenge uuid;
begin
  select * into account from hub_private.accounts
  where case when strpos(p_identifier,'@')>0 then email=lower(trim(p_identifier))
    else username=lower(trim(p_identifier)) end for update;
  if account.user_id is null then return jsonb_build_object('error','invalid'); end if;
  if account.last_recovery_at>now()-interval '60 seconds' then return jsonb_build_object('error','cooldown'); end if;
  delete from hub_private.recovery where user_id=account.user_id or expires_at<now();
  insert into hub_private.recovery(user_id) values(account.user_id) returning id into challenge;
  update hub_private.accounts set last_recovery_at=now() where user_id=account.user_id;
  return jsonb_build_object('challengeId',challenge,'email',account.email);
end $$;

create function public.hub_recovery_step(p_id uuid,p_action text)
returns jsonb language plpgsql security definer set search_path='' as $$
declare challenge hub_private.recovery; account hub_private.accounts;
begin
  select * into challenge from hub_private.recovery where id=p_id;
  if challenge.id is null then return jsonb_build_object('error','expired'); end if;
  -- Lock in the same account-then-challenge order as recovery_begin.
  select * into account from hub_private.accounts where user_id=challenge.user_id for update;
  select * into challenge from hub_private.recovery where id=p_id for update;
  if challenge.id is null or challenge.consumed or challenge.expires_at<=now() then return jsonb_build_object('error','expired'); end if;
  if p_action='consume' then
    update hub_private.recovery set consumed=true where id=p_id;
    return jsonb_build_object('ok',true);
  end if;
  if challenge.attempts>=3 then return jsonb_build_object('error','expired'); end if;
  if p_action='resend' then
    if account.last_recovery_at>now()-interval '60 seconds' then return jsonb_build_object('error','cooldown'); end if;
    update hub_private.accounts set last_recovery_at=now() where user_id=challenge.user_id;
    update hub_private.recovery set sent_at=now() where id=p_id;
  elsif p_action='verify' then
    update hub_private.recovery set attempts=attempts+1 where id=p_id;
  else return jsonb_build_object('error','invalid'); end if;
  return jsonb_build_object('email',account.email,'remaining',3-challenge.attempts-case when p_action='verify' then 1 else 0 end);
end $$;

revoke all on function public.hub_auth_throttle(text),public.hub_auth_lookup(text),public.hub_recovery_begin(text),public.hub_recovery_step(uuid,text) from public,anon,authenticated;
grant execute on function public.hub_auth_throttle(text),public.hub_auth_lookup(text),public.hub_recovery_begin(text),public.hub_recovery_step(uuid,text) to service_role;
commit;
