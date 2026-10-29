// Isolated PostgreSQL/WASM integration tests. No network or hosted user accounts.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createHandler } from './supabase/functions/sounds-hub/handler.mjs';
import { PGlite } from '@electric-sql/pglite';

test('account SQL and Edge Function integration', async t => {
  const db = new PGlite();
  const id='11111111-1111-4111-8111-111111111111';
  await db.exec(`create role anon; create role authenticated; create role service_role bypassrls;
    create schema auth;
    create table auth.users(id uuid primary key,email text,raw_user_meta_data jsonb);
    insert into auth.users values('${id}','test@example.test','{"display_name":"LegacyUser"}');`);
  await db.exec(await readFile(new URL('./supabase/setup-account-flow.sql',import.meta.url),'utf8'));
  const rpc = async (name, body) => {
    assert.match(name,/^hub_(auth_(lookup|throttle)|recovery_(begin|step))$/);
    const values=Object.values(body), placeholders=values.map((_,i)=>'$'+(i+1)).join(',');
    const result = await db.query(`select public.${name}(${placeholders}) as value`,values);
    return result.rows[0].value;
  };
  let mail=0, passwords=0;
  const session={access_token:'access',refresh_token:'refresh',expires_in:3600,user:{id,user_metadata:{display_name:'LegacyUser'}}};
  const handler=createHandler({url:'https://test.supabase.co',anonKey:'anon',serviceKey:'service'},async (uri,init={}) => {
    const url=new URL(uri), body=init.body?JSON.parse(init.body):{};
    if(url.pathname.startsWith('/rest/v1/rpc/')) return Response.json(await rpc(url.pathname.split('/').at(-1),body));
    if(url.pathname==='/auth/v1/recover') { assert.equal(body.email,'test@example.test'); mail++; return Response.json({}); }
    if(url.pathname==='/auth/v1/token') { passwords++; return body.password==='123456'?Response.json(session):new Response('',{status:400}); }
    if(url.pathname==='/auth/v1/verify') return body.token==='12345678'?Response.json(session):new Response('',{status:403});
    throw new Error('Unexpected upstream '+url.pathname);
  });
  const call=async(route,body) => handler(new Request('https://test.supabase.co/functions/v1/sounds-hub/auth/'+route,{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify(body)}));
  const ageCooldown=()=>db.exec(`update hub_private.accounts set last_recovery_at=now()-interval '61 seconds';`);
  let challengeId;
  await t.test('account checks match username or email without sending mail or disclosing an email',async()=>{
    for(const identifier of ['LEGACYUSER','TEST@EXAMPLE.TEST']) {
      const response=await call('check',{identifier}); assert.equal(response.status,200);
      assert.deepEqual(await response.json(),{exists:true});
    }
    assert.deepEqual(await (await call('check',{identifier:'missing'})).json(),{exists:false});
    assert.equal(mail,0);
  });
  await t.test('username lookup is case-insensitive and never returns an email without authentication',async()=>{
    let response=await call('login',{identifier:'LEGACYUSER',password:'bad'});
    assert.equal(response.status,400); assert.ok(!(await response.text()).includes('test@example.test'));
    response=await call('login',{identifier:'LegacyUser',password:'123456'}); assert.equal(response.status,200);
    assert.equal((await response.json()).access_token,'access'); assert.equal(passwords,2);
  });
  await t.test('unknown usernames and emails cannot trigger recovery email',async()=>{
    for(const identifier of ['missing','missing@example.test']) {
      assert.equal((await call('recover',{identifier})).status,400);
    }
    assert.equal(mail,0);
  });
  await t.test('matching username sends to its email and returns an opaque challenge only',async()=>{
    const response=await call('recover',{identifier:'legacyuser'}); assert.equal(response.status,200);
    const body=await response.json(); challengeId=body.challengeId;
    assert.ok(challengeId); assert.deepEqual(Object.keys(body),['challengeId']); assert.equal(mail,1);
  });
  await t.test('60-second cooldown applies to resend and restarting via either alias',async()=>{
    assert.equal((await call('resend',{challengeId})).status,429);
    assert.equal((await call('recover',{identifier:'test@example.test'})).status,429);
    assert.equal(mail,1);
    await ageCooldown(); assert.equal((await call('resend',{challengeId})).status,200); assert.equal(mail,2);
  });
  await t.test('three guesses are recorded in SQL and resending cannot reset the attempt count',async()=>{
    for(let remaining=2;remaining>=0;remaining--) {
      const response=await call('verify',{challengeId,token:'00000000'});
      assert.equal(response.status,200); assert.deepEqual(await response.json(),{verified:false,remaining});
      if(remaining===1) { await ageCooldown(); assert.equal((await call('resend',{challengeId})).status,200); }
    }
    assert.equal((await call('verify',{challengeId,token:'12345678'})).status,410);
    assert.equal((await call('resend',{challengeId})).status,410);
  });
  await t.test('restarting creates a fresh process and a valid code can succeed on the third try',async()=>{
    await ageCooldown(); const response=await call('recover',{identifier:'TEST@EXAMPLE.TEST'});
    const next=(await response.json()).challengeId; assert.notEqual(next,challengeId);
    assert.equal((await call('verify',{challengeId,token:'12345678'})).status,410); challengeId=next;
    await call('verify',{challengeId,token:'wrong'}); await call('verify',{challengeId,token:'00000000'});
    const result=await call('verify',{challengeId,token:'12345678'}); assert.equal(result.status,200);
    assert.equal((await result.json()).verified,true);
    assert.equal((await call('verify',{challengeId,token:'12345678'})).status,410);
  });
  await t.test('expired recovery processes require restart',async()=>{
    await ageCooldown(); challengeId=(await (await call('recover',{identifier:'legacyuser'})).json()).challengeId;
    await db.exec(`update hub_private.recovery set expires_at=now()-interval '1 second';`);
    assert.equal((await call('verify',{challengeId,token:'12345678'})).status,410);
  });
  await t.test('new usernames are unique ignoring case and stay in sync with email changes',async()=>{
    const second='22222222-2222-4222-8222-222222222222';
    await db.query('insert into auth.users values($1,$2,$3)',[second,'second@example.test',{username:'NewUser'}]);
    assert.equal((await rpc('hub_auth_lookup',{p_identifier:'NEWUSER'})).email,'second@example.test');
    await assert.rejects(db.query('insert into auth.users values($1,$2,$3)',['33333333-3333-4333-8333-333333333333','third@example.test',{username:'newuser'}]));
    await db.query('update auth.users set email=$1 where id=$2',['changed@example.test',second]);
    assert.equal((await rpc('hub_auth_lookup',{p_identifier:'newuser'})).email,'changed@example.test');
    assert.equal(await rpc('hub_auth_lookup',{p_identifier:'second@example.test'}),null);
  });
  await t.test('anonymous and authenticated roles cannot read emails or execute private auth RPCs',async()=>{
    for(const role of ['anon','authenticated']) {
      for(const signature of ['public.hub_auth_lookup(text)','public.hub_auth_throttle(text)','public.hub_recovery_begin(text)','public.hub_recovery_step(uuid,text)']) {
        const r=await db.query('select has_function_privilege($1,$2,\'EXECUTE\') as allowed',[role,signature]); assert.equal(r.rows[0].allowed,false);
      }
      const r=await db.query('select has_schema_privilege($1,\'hub_private\',\'USAGE\') as allowed',[role]); assert.equal(r.rows[0].allowed,false);
    }
    const r=await db.query("select relrowsecurity from pg_class join pg_namespace on pg_namespace.oid=relnamespace where nspname='hub_private' and relkind='r'");
    assert.equal(r.rows.length,3); assert.ok(r.rows.every(row=>row.relrowsecurity));
  });
  await t.test('per-account auth throttling persists across requests',async()=>{
    for(let i=0;i<20;i++) assert.equal(await rpc('hub_auth_throttle',{p_key:'fresh-limiter-key'}),true);
    assert.equal(await rpc('hub_auth_throttle',{p_key:'fresh-limiter-key'}),false);
  });
  await db.close();
});
