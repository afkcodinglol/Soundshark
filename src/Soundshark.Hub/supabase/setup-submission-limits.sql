-- Apply after OwnerReportsSetup.sql. Safe to rerun. Existing reports are preserved.
begin;
alter table public.hub_support_reports drop constraint if exists hub_support_reports_kind_check;
alter table public.hub_support_reports add constraint hub_support_reports_kind_check check(kind in ('issue','sound','suggestion'));
-- One function with an optional category keeps older desktop clients compatible.
drop function if exists public.hub_create_issue(uuid,text,jsonb);
create or replace function public.hub_create_issue(p_user_id uuid,p_body text,p_files jsonb,p_kind text default 'issue')
returns uuid language plpgsql security definer set search_path='' as $$
declare report_id uuid; display text;
begin
  perform 1 from auth.users where id=p_user_id for update;
  if not found then return null; end if;
  if p_kind not in ('issue','suggestion') or p_kind is null or char_length(trim(p_body)) not between 10 and 8000
     or jsonb_typeof(p_files)<>'array' or jsonb_array_length(p_files)>5 or octet_length(p_files::text)>3*1024*1024 then raise exception 'Invalid report'; end if;
  if not public.hub_is_owner(p_user_id) then
    if p_kind='suggestion' and (select count(*) from public.hub_support_reports where user_id=p_user_id and kind='suggestion' and created_at>now()-interval '24 hours')>=5 then return null; end if;
    if p_kind='issue' and (select count(*) from public.hub_support_reports where user_id=p_user_id and kind in ('issue','sound') and created_at>now()-interval '12 hours')>=6 then return null; end if;
  end if;
  select coalesce(username,'Soundshark user') into display from hub_private.accounts where user_id=p_user_id;
  insert into public.hub_support_reports(user_id,username,kind,body,files)
  values(p_user_id,coalesce(display,'Soundshark user'),p_kind,trim(p_body),p_files) returning id into report_id;
  return report_id;
end $$;
revoke all on function public.hub_create_issue(uuid,text,jsonb,text) from public,anon,authenticated;
grant execute on function public.hub_create_issue(uuid,text,jsonb,text) to service_role;
create or replace function public.hub_submit_sound_report(p_sound_id uuid,p_user_id uuid)
returns boolean language plpgsql security definer set search_path='' as $$
declare accepted boolean; display text; sound_title text;
begin
  perform 1 from auth.users where id=p_user_id for update;
  if not found then return false; end if;
  if exists(select 1 from public.hub_support_reports where user_id=p_user_id and sound_id=p_sound_id and kind='sound') then return true; end if;
  if not public.hub_is_owner(p_user_id) and (select count(*) from public.hub_support_reports where user_id=p_user_id and kind in ('issue','sound') and created_at>now()-interval '12 hours')>=6 then
    raise exception using errcode='PT429',message='Report limit reached: six reports per 12 hours';
  end if;
  accepted:=public.hub_report_sound(p_sound_id=>p_sound_id,p_user_id=>p_user_id);
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
commit;
