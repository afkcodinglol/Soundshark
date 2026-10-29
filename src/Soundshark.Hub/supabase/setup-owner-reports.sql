-- Apply AFTER AccountSetup.sql, then deploy the regenerated sounds-hub.ts.
-- Assign the existing Pheex account once. No client can assign or inherit this role.
begin;
create table if not exists public.hub_owners (
  user_id uuid primary key references auth.users(id) on delete cascade
);
alter table public.hub_owners enable row level security;
revoke all on public.hub_owners from public,anon,authenticated;
grant all on public.hub_owners to service_role;
do $$
begin
  if not exists(select 1 from public.hub_owners) then
    insert into public.hub_owners(user_id)
    select user_id from hub_private.accounts where username='pheex';
    if not found then raise exception 'Existing Pheex account was not found. No Owner role was assigned.'; end if;
  end if;
end $$;

create or replace function public.hub_is_owner(p_user_id uuid)
returns boolean language sql stable security definer set search_path='' as $$
  select exists(select 1 from public.hub_owners where user_id=p_user_id)
$$;
revoke all on function public.hub_is_owner(uuid) from public,anon,authenticated;
grant execute on function public.hub_is_owner(uuid) to service_role;

create table if not exists public.hub_support_reports (
  id uuid primary key default gen_random_uuid(),
  user_id uuid not null references auth.users(id) on delete cascade,
  username text not null,
  kind text not null check(kind in ('issue','sound')),
  body text not null check(char_length(body) between 1 and 8000),
  sound_id uuid,
  files jsonb not null default '[]' check(jsonb_typeof(files)='array' and jsonb_array_length(files)<=5),
  status text not null default 'open' check(status in ('open','resolved','unfixable')),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);
create index if not exists hub_support_status_created on public.hub_support_reports(status,created_at desc,id desc);
create index if not exists hub_support_user_created on public.hub_support_reports(user_id,created_at desc);
create unique index if not exists hub_support_sound_user on public.hub_support_reports(sound_id,user_id) where kind='sound';
alter table public.hub_support_reports enable row level security;
revoke all on public.hub_support_reports from public,anon,authenticated;
grant all on public.hub_support_reports to service_role;

create or replace function public.hub_create_issue(p_user_id uuid,p_body text,p_files jsonb)
returns uuid language plpgsql security definer set search_path='' as $$
declare report_id uuid; display text;
begin
  -- Lock the account so concurrent requests cannot race the report quota.
  perform 1 from auth.users where id=p_user_id for update;
  if not found then return null; end if;
  if char_length(trim(p_body)) not between 10 and 8000 or jsonb_typeof(p_files)<>'array' or jsonb_array_length(p_files)>5
     or octet_length(p_files::text)>3*1024*1024 then raise exception 'Invalid report'; end if;
  if not public.hub_is_owner(p_user_id) and (select count(*) from public.hub_support_reports
      where user_id=p_user_id and kind='issue' and created_at>now()-interval '1 day')>=5 then return null; end if;
  select coalesce(username,'Soundshark user') into display from hub_private.accounts where user_id=p_user_id;
  insert into public.hub_support_reports(user_id,username,kind,body,files)
  values(p_user_id,coalesce(display,'Soundshark user'),'issue',trim(p_body),p_files) returning id into report_id;
  return report_id;
end $$;
revoke all on function public.hub_create_issue(uuid,text,jsonb) from public,anon,authenticated;
grant execute on function public.hub_create_issue(uuid,text,jsonb) to service_role;

create or replace function public.hub_submit_sound_report(p_sound_id uuid,p_user_id uuid)
returns boolean language plpgsql security definer set search_path='' as $$
declare accepted boolean; display text; sound_title text;
begin
  accepted := public.hub_report_sound(p_sound_id=>p_sound_id,p_user_id=>p_user_id);
  if not accepted then return false; end if;
  select coalesce(username,'Soundshark user') into display from hub_private.accounts where user_id=p_user_id;
  select title into sound_title from public.hub_sounds where id=p_sound_id;
  insert into public.hub_support_reports(user_id,username,kind,body,sound_id)
  values(p_user_id,coalesce(display,'Soundshark user'),'sound','Sound reported for review: '||coalesce(sound_title,p_sound_id::text),p_sound_id)
  on conflict(sound_id,user_id) where kind='sound' do nothing;
  return true;
end $$;
revoke all on function public.hub_submit_sound_report(uuid,uuid) from public,anon,authenticated;
grant execute on function public.hub_submit_sound_report(uuid,uuid) to service_role;

-- Preserve existing moderation reports in the new Owner inbox. JSON access handles
-- the older reporter_id/user_id spelling without changing the original tables.
insert into public.hub_support_reports(user_id,username,kind,body,sound_id)
select a.user_id,coalesce(a.username,'Soundshark user'),'sound',
  'Sound reported for review: '||coalesce(s.title,(to_jsonb(r)->>'sound_id')),
  (to_jsonb(r)->>'sound_id')::uuid
from public.hub_reports r
join hub_private.accounts a on a.user_id::text=coalesce(to_jsonb(r)->>'user_id',to_jsonb(r)->>'reporter_id')
join public.hub_sounds s on s.id::text=to_jsonb(r)->>'sound_id'
on conflict(sound_id,user_id) where kind='sound' do nothing;
commit;
