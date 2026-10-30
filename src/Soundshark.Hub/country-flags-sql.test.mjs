import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { PGlite } from '@electric-sql/pglite';

test('country preference migration updates existing uploads atomically',async()=>{
 const db=new PGlite();
 await db.exec(`create role anon; create role authenticated; create role service_role;
   create schema auth; create table auth.users(id uuid primary key);
   create table public.hub_sounds(id uuid primary key,owner_id uuid not null references auth.users(id));`);
 const sql=await readFile(new URL('./supabase/setup-country-flags.sql',import.meta.url),'utf8');
 await db.exec(sql);
 assert.equal((await db.query(`select has_table_privilege('service_role','public.hub_profiles','select') allowed`)).rows[0].allowed,true);
 assert.equal((await db.query(`select has_table_privilege('authenticated','public.hub_profiles','select') allowed`)).rows[0].allowed,false);
 const user='11111111-1111-4111-8111-111111111111', sound='22222222-2222-4222-8222-222222222222';
 await db.exec(`insert into auth.users values('${user}');insert into public.hub_sounds(id,owner_id) values('${sound}','${user}');`);
 await db.query(`select public.hub_set_country_preference($1,$2,$3)`,[user,'CA',true]);
 assert.deepEqual((await db.query(`select country_code from public.hub_sounds where id=$1`,[sound])).rows,[{country_code:'CA'}]);
 assert.deepEqual((await db.query(`select country_code,show_country from public.hub_profiles where user_id=$1`,[user])).rows,[{country_code:'CA',show_country:true}]);
 await db.query(`select public.hub_set_country_preference($1,$2,$3)`,[user,'CA',false]);
 assert.equal((await db.query(`select country_code from public.hub_sounds where id=$1`,[sound])).rows[0].country_code,null);
 await db.close();
});
