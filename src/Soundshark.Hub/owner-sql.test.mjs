import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {PGlite} from '@electric-sql/pglite';
import {createHandler} from './supabase/functions/sounds-hub/handler.mjs';

test('Owner and private report SQL / Edge integration',async t=>{
 const db=new PGlite();
 const owner='11111111-1111-4111-8111-111111111111', other='22222222-2222-4222-8222-222222222222', sound='33333333-3333-4333-8333-333333333333';
 await db.exec(`create role anon;create role authenticated;create role service_role bypassrls;
 create schema auth;create table auth.users(id uuid primary key,email text,raw_user_meta_data jsonb);
 insert into auth.users values('${owner}','owner@example.test','{"username":"Pheex"}'),('${other}','other@example.test','{"username":"tester"}');
 create table public.hub_sounds(id uuid primary key,title text);
 insert into public.hub_sounds values('${sound}','Reported audio');
 create table public.hub_reports(sound_id uuid,reporter_id uuid);
 insert into public.hub_reports values('${sound}','${other}');
 create function public.hub_report_sound(p_sound_id uuid,p_user_id uuid) returns boolean language sql as $$select true$$;`);
 await db.exec(await readFile(new URL('./supabase/setup-account-flow.sql',import.meta.url),'utf8'));
 const sql=await readFile(new URL('./supabase/setup-owner-reports.sql',import.meta.url),'utf8');
 await db.exec(sql);
 await db.exec(await readFile(new URL('./supabase/setup-submission-limits.sql',import.meta.url),'utf8'));
 const rpc=async(name,body)=>{
  assert.ok(['hub_is_owner','hub_create_issue','hub_submit_sound_report'].includes(name));
  const values=Object.values(body).map(v=>Array.isArray(v)?JSON.stringify(v):v);
  return (await db.query(`select public.${name}(${values.map((_,i)=>'$'+(i+1)).join(',')}) as value`,values)).rows[0].value;
 };
 let current=other, reads=0, writes=0;
 const handler=createHandler({url:'https://test.supabase.co',anonKey:'public',serviceKey:'secret'},async(uri,init={})=>{
  const url=new URL(uri);
  if(url.pathname==='/auth/v1/user') return Response.json({id:current,email_confirmed_at:'2026-10-29',user_metadata:{username:'Pheex',is_owner:true}});
  if(url.pathname.startsWith('/rest/v1/rpc/'))return Response.json(await rpc(url.pathname.split('/').at(-1),JSON.parse(init.body)));
  if(url.pathname==='/rest/v1/hub_support_reports'){
   if(init.method==='PATCH'){
    writes++;const body=JSON.parse(init.body);const result=await db.query('update public.hub_support_reports set status=$1 where id=$2 returning *',[body.status,url.searchParams.get('id').slice(3)]);return Response.json(result.rows);
   }
   reads++;if(url.searchParams.has('id'))return Response.json((await db.query('select * from public.hub_support_reports where id=$1',[url.searchParams.get('id').slice(3)])).rows);
   return Response.json((await db.query('select id,username,kind,body,sound_id,status,created_at,updated_at from public.hub_support_reports where status=$1 order by created_at desc limit 41 offset $2',[url.searchParams.get('status').slice(3),Number(url.searchParams.get('offset'))])).rows);
  }
  throw new Error('Unexpected upstream '+url);
 });
 const call=(route,method='GET',body)=>handler(new Request('https://test.supabase.co/functions/v1/sounds-hub'+route,{method,headers:{authorization:'Bearer valid','content-type':'application/json'},body:body?JSON.stringify(body):undefined}));
 let report;
 await t.test('Pheex is seeded once and previous sound reports appear in Owner inbox',async()=>{
  assert.equal(await rpc('hub_is_owner',{p_user_id:owner}),true);assert.equal(await rpc('hub_is_owner',{p_user_id:other}),false);
  assert.equal((await db.query("select count(*)::int n from public.hub_support_reports where kind='sound'")).rows[0].n,1);
 });
 await t.test('client roles cannot grant Owner, inspect reports, or invoke privileged RPCs',async()=>{
  for(const role of ['anon','authenticated'])for(const query of ['select * from public.hub_owners','select * from public.hub_support_reports',`select public.hub_is_owner('${owner}')`,`insert into public.hub_owners values('${other}')`]){
   await db.exec('set role '+role);await assert.rejects(db.query(query),/permission denied/);await db.exec('reset role');
  }
 });
 await t.test('changing usernames does not transfer Owner status',async()=>{
  await db.exec(`update hub_private.accounts set username='renamed' where user_id='${owner}';update hub_private.accounts set username='pheex' where user_id='${other}';`);
  await db.exec(sql);
  await db.exec(await readFile(new URL('./supabase/setup-submission-limits.sql',import.meta.url),'utf8'));
  assert.equal(await rpc('hub_is_owner',{p_user_id:other}),false);assert.equal(await rpc('hub_is_owner',{p_user_id:owner}),true);
 });
 await t.test('normal account metadata cannot grant server permissions',async()=>{
  assert.deepEqual(await (await call('/account')).json(),{isOwner:false});assert.equal((await call('/admin/reports')).status,403);assert.equal(reads,0);
 });
 await t.test('five logs are stored privately and reports are delivered to Owner',async()=>{
  const response=await call('/issues','POST',{message:'A useful suggestion for Soundshark',files:Array.from({length:5},(_,i)=>({name:'log'+i+'.txt',text:'Diagnostic data '+i}))});assert.equal(response.status,201);report=(await response.json()).id;
  current=owner;assert.equal((await call('/admin/reports')).status,200);const detail=await (await call('/admin/reports/'+report)).json();assert.equal(detail.files.length,5);assert.equal(detail.status,'open');
 });
 await t.test('six logs, binary content, duplicate names, path traversal and oversized logs are rejected',async()=>{
  current=other;
  for(const files of [Array.from({length:6},(_,i)=>({name:i+'.log',text:'x'})),[{name:'x.log',text:'a\0b'}],[{name:'x.log',text:'a'},{name:'X.LOG',text:'b'}],[{name:'../x.log',text:'x'}],[{name:'x.log',text:'a'.repeat(512*1024+1)}]])
   assert.equal((await call('/issues','POST',{message:'A valid message for this test',files})).status,400);
 });
 await t.test('normal users cannot read a guessed report ID or change status',async()=>{
  assert.equal((await call('/admin/reports/'+report)).status,403);assert.equal((await call('/admin/reports/'+report,'PATCH',{status:'resolved'})).status,403);assert.equal(writes,0);
 });
 await t.test('Owner can mark resolved, unfixable and reopen with strict status validation',async()=>{
  current=owner;for(const status of ['resolved','unfixable','open']){
   assert.equal((await call('/admin/reports/'+report,'PATCH',{status})).status,200);assert.equal((await (await call('/admin/reports/'+report)).json()).status,status);
  }
  assert.equal((await call('/admin/reports/'+report,'PATCH',{status:'arbitrary'})).status,400);assert.equal(writes,3);
 });
 await t.test('persistent report quota applies to users and Owner is exempt',async()=>{
  current=other;for(let i=0;i<4;i++)assert.equal((await call('/issues','POST',{message:'Another useful suggestion',files:[]})).status,201);
  assert.equal((await call('/issues','POST',{message:'Over the daily report quota',files:[]})).status,429);
  current=owner;for(let i=0;i<6;i++)assert.equal((await call('/issues','POST',{message:'Owner report quota is exempt',files:[]})).status,201);
 });
 await t.test('sound reports are copied into inbox once per reporter and sound',async()=>{
  assert.equal(await rpc('hub_submit_sound_report',{p_sound_id:sound,p_user_id:other}),true);
  assert.equal((await db.query("select count(*)::int n from public.hub_support_reports where kind='sound'")).rows[0].n,1);
 });
 await t.test('suggestions have a separate rolling five-per-24-hours quota',async()=>{
  current=other;
  for(let i=0;i<5;i++)assert.equal((await call('/issues','POST',{kind:'suggestion',message:'A new suggestion for the app',files:[]})).status,201);
  const rejected=await call('/issues','POST',{kind:'suggestion',message:'Suggestion number six is limited',files:[]});
  assert.equal(rejected.status,429);assert.equal((await rejected.json()).error_code,'suggestion_limit');
  await db.exec(`update public.hub_support_reports set status='resolved' where user_id='${other}'`);
  assert.equal((await call('/issues','POST',{kind:'suggestion',message:'Resolved suggestions still count',files:[]})).status,429);
  await db.exec(`update public.hub_support_reports set created_at=now()-interval '24 hours 1 second' where user_id='${other}' and kind='suggestion'`);
  assert.equal((await call('/issues','POST',{kind:'suggestion',message:'Expired suggestions free a slot',files:[]})).status,201);
 });
 await t.test('problem and sound reports share six per rolling 12 hours',async()=>{
  current=other;
  const blocked=await call('/issues','POST',{kind:'issue',message:'Reports are already at their quota',files:[]});assert.equal(blocked.status,429);assert.equal((await blocked.json()).error_code,'report_limit');
  const newSound='44444444-4444-4444-8444-444444444444';
  await db.exec(`insert into public.hub_sounds values('${newSound}','Another sound')`);
  await assert.rejects(rpc('hub_submit_sound_report',{p_sound_id:newSound,p_user_id:other}),e=>e.code==='PT429');
  await db.exec(`update public.hub_support_reports set created_at=now()-interval '12 hours 1 second' where user_id='${other}' and kind in ('issue','sound')`);
  assert.equal(await rpc('hub_submit_sound_report',{p_sound_id:newSound,p_user_id:other}),true);
  for(let i=0;i<5;i++)assert.equal((await call('/issues','POST',{kind:'issue',message:'A problem report in the new window',files:[]})).status,201);
  assert.equal((await call('/issues','POST',{kind:'issue',message:'Seventh report should be blocked',files:[]})).status,429);
 });
 await db.close();
});
