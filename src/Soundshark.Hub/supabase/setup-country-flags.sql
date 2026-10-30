-- Apply once after the Sounds Hub catalogue migration, then redeploy the Edge
-- Function. Country choices remain private unless show_country is enabled.
begin;

create table if not exists public.hub_profiles (
  user_id uuid primary key references auth.users(id) on delete cascade,
  country_code text,
  show_country boolean not null default false,
  updated_at timestamptz not null default now(),
  constraint hub_profile_country_shape check (
    country_code is null or country_code ~ '^[A-Z]{2}(-[A-Z]{3})?$'
  )
);
alter table public.hub_profiles enable row level security;
revoke all on table public.hub_profiles from public, anon, authenticated;
grant select on table public.hub_profiles to service_role;

alter table public.hub_sounds add column if not exists country_code text;
do $$ begin
  if not exists (select 1 from pg_constraint where conname='hub_sound_country_shape') then
    alter table public.hub_sounds add constraint hub_sound_country_shape
      check (country_code is null or country_code ~ '^[A-Z]{2}(-[A-Z]{3})?$');
  end if;
end $$;

create or replace function public.hub_set_country_preference(
  p_user_id uuid, p_country_code text, p_show_country boolean
) returns void language plpgsql security definer set search_path='' as $$
begin
  if p_country_code is null or p_country_code !~ '^[A-Z]{2}(-[A-Z]{3})?$' then
    raise exception 'Invalid country code';
  end if;
  insert into public.hub_profiles(user_id,country_code,show_country,updated_at)
  values(p_user_id,p_country_code,p_show_country,now())
  on conflict(user_id) do update set country_code=excluded.country_code,
    show_country=excluded.show_country,updated_at=excluded.updated_at;
  update public.hub_sounds set country_code=case when p_show_country then p_country_code else null end
  where owner_id=p_user_id;
end $$;
revoke all on function public.hub_set_country_preference(uuid,text,boolean) from public,anon,authenticated;
grant execute on function public.hub_set_country_preference(uuid,text,boolean) to service_role;

commit;
