import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { inspectMp3, MAX_AUDIO_BYTES } from './supabase/functions/sounds-hub/audio.mjs';
import { createHandler } from './supabase/functions/sounds-hub/handler.mjs';

const userId='11111111-1111-4111-8111-111111111111';
const otherId='22222222-2222-4222-8222-222222222222';
const audio = new Uint8Array(execFileSync(process.env.FFMPEG || fileURLToPath(new URL('../../tools/ffmpeg.exe',import.meta.url)), ['-nostdin','-v','error','-f','lavfi','-i','sine=frequency=440:duration=1','-ar','48000','-ac','2','-c:a','libmp3lame','-b:a','192k','-id3v2_version','0','-write_id3v1','0','-f','mp3','pipe:1']));
const row = { id:'33333333-3333-4333-8333-333333333333', owner_id:userId, title:'Sound', uploader:'Devon', duration:inspectMp3(audio).duration, bytes:audio.length, sha256:'a'.repeat(64), object_path:userId+'/33333333-3333-4333-8333-333333333333.mp3',status:'published' };
function service(options={}) {
  const calls=[];
  const handler=createHandler({url:'https://test.supabase.co',anonKey:'anon',serviceKey:'server-only'},async (uri,init={})=>{
    const url=new URL(uri); calls.push({url,init});
    if(url.pathname==='/auth/v1/user') return Response.json({id:options.userId||userId,email_confirmed_at:options.unverified?null:'2026-10-29',user_metadata:{display_name:'Devon'}});
    if(url.pathname==='/rest/v1/rpc/hub_is_owner') return Response.json(options.owner===true);
    if(url.pathname==='/rest/v1/hub_owners') return Response.json(options.owner?[{user_id:options.userId||userId}]:[]);
    if(url.pathname==='/rest/v1/rpc/hub_reserve_upload') return Response.json(!options.quota);
    if(url.pathname==='/rest/v1/rpc/hub_submit_sound_report') return options.reportQuota?Response.json({message:'limit'},{status:429}):Response.json(true);
    if(url.pathname==='/rest/v1/hub_sounds' && (!init.method||init.method==='GET')) return Response.json(options.rows||[row]);
    if(url.pathname==='/rest/v1/hub_sounds' && init.method==='POST' && options.failInsert) return new Response('private error',{status:500});
    if(url.pathname==='/storage/v1/object/hub-audio' && init.method==='DELETE' && options.failDelete) return new Response('private error',{status:503});
    if(url.pathname.startsWith('/storage/v1/object/') && (!init.method||init.method==='GET')) return new Response(audio);
    return new Response(null,{status:204});
  }); return {handler,calls};
}
const request=(path,init={})=>new Request('https://test.supabase.co/functions/v1/sounds-hub'+path,init);
function upload(bytes=audio,rights='confirmed') { const form=new FormData();form.set('title','A shared sound');form.set('rights',rights);form.set('file',new Blob([bytes],{type:'audio/mpeg'}),'sound.mp3');return request('/upload',{method:'POST',headers:{authorization:'Bearer user-token'},body:form}); }

test('real FFmpeg MP3 is parsed within its expected encoded duration',()=>{const info=inspectMp3(audio);assert.ok(info.duration>1&&info.duration<1.2);assert.equal(info.bytes,audio.length);});
test('rejects executables, truncated streams and excessive audio sizes',()=>{for(const bytes of [new Uint8Array([77,90,1,2]),audio.slice(0,-1),new Uint8Array(MAX_AUDIO_BYTES+1)])assert.throws(()=>inspectMp3(bytes));});
test('rejects audio over sixty seconds',()=>{const frame=new Uint8Array(576);frame.set([255,251,180,0]);const long=new Uint8Array(frame.length*2600);for(let i=0;i<2600;i++)long.set(frame,i*frame.length);assert.throws(()=>inspectMp3(long));});
test('public browse restricts catalogue to published sounds and limits pagination',async()=>{const {handler,calls}=service();const result=await handler(request('/sounds?q=hello%26owner_id%3Devil&page=0'));assert.equal(result.status,200);const db=calls.find(c=>c.url.pathname==='/rest/v1/hub_sounds');assert.equal(db.url.searchParams.get('status'),'eq.published');assert.equal(db.url.searchParams.get('limit'),'41');assert.equal(db.url.searchParams.get('owner_id'),null);assert.equal((await handler(request('/sounds?page=-1'))).status,400);});
test('anonymous upload is rejected before any privileged request',async()=>{const {handler,calls}=service();assert.equal((await handler(request('/upload',{method:'POST'}))).status,401);assert.equal(calls.length,0);});
test('unverified accounts cannot upload',async()=>{const {handler,calls}=service({unverified:true});assert.equal((await handler(upload())).status,403);assert.equal(calls.length,1);});
test('upload validates audio and rights before reserving storage',async()=>{const {handler,calls}=service();assert.equal((await handler(upload(new Uint8Array([77,90])))).status,400);assert.equal((await handler(upload(audio,'no'))).status,400);assert.ok(!calls.some(c=>c.url.pathname.includes('/storage/')));});
test('server quota prevents writing storage',async()=>{const {handler,calls}=service({quota:true});assert.equal((await handler(upload())).status,429);assert.ok(!calls.some(c=>c.url.pathname.includes('/storage/')));});
test('verified upload stores canonical ownership and computes hash server-side',async()=>{const {handler,calls}=service();const result=await handler(upload());assert.equal(result.status,201);const sound=await result.json();assert.equal(sound.ownerId,userId);assert.equal(sound.sha256.length,64);const insert=calls.find(c=>c.url.pathname==='/rest/v1/hub_sounds'&&c.init.method==='POST');const saved=JSON.parse(insert.init.body);assert.equal(saved.owner_id,userId);assert.equal(saved.object_path,userId+'/'+sound.id+'.mp3');});
test('metadata publication failure removes the stored object',async()=>{const {handler,calls}=service({failInsert:true});assert.equal((await handler(upload())).status,503);assert.ok(calls.some(c=>c.url.pathname==='/storage/v1/object/hub-audio'&&c.init.method==='DELETE'));});
test('another user cannot remove an upload',async()=>{const {handler,calls}=service({userId:otherId,rows:[]});assert.equal((await handler(request('/sounds/'+row.id,{method:'DELETE',headers:{authorization:'Bearer other-token'}}))).status,404);assert.ok(!calls.some(c=>c.init.method==='DELETE'));});
test('owner removal hides listing before storage deletion and only then removes metadata',async()=>{const {handler,calls}=service();assert.equal((await handler(request('/sounds/'+row.id,{method:'DELETE',headers:{authorization:'Bearer owner-token'}}))).status,200);const mutations=calls.filter(c=>['PATCH','DELETE'].includes(c.init.method));assert.deepEqual(mutations.map(c=>c.init.method),['PATCH','DELETE','DELETE']);assert.equal(mutations[0].url.pathname,'/rest/v1/hub_sounds');assert.equal(mutations[1].url.pathname,'/storage/v1/object/hub-audio');assert.equal(mutations[2].url.pathname,'/rest/v1/hub_sounds');});
test('failed storage deletion retains owner metadata so removal can be retried',async()=>{const {handler,calls}=service({failDelete:true});assert.equal((await handler(request('/sounds/'+row.id,{method:'DELETE',headers:{authorization:'Bearer owner-token'}}))).status,503);assert.ok(calls.some(c=>c.init.method==='PATCH'));assert.ok(!calls.some(c=>c.url.pathname==='/rest/v1/hub_sounds'&&c.init.method==='DELETE'));});
test('reported or removed sounds cannot be downloaded',async()=>{const {handler,calls}=service({rows:[]});assert.equal((await handler(request('/sounds/'+row.id+'/audio'))).status,404);assert.ok(!calls.some(c=>c.url.pathname.includes('/storage/')));});
test('successful download is streamed as audio without executing content',async()=>{const {handler}=service();const result=await handler(request('/sounds/'+row.id+'/audio'));assert.equal(result.status,200);assert.equal(result.headers.get('x-content-type-options'),'nosniff');assert.deepEqual(new Uint8Array(await result.arrayBuffer()),audio);});

test('Owner can delete another uploader sound using the actual uploader storage path',async()=>{
 const {handler,calls}=service({owner:true,userId:otherId});
 assert.equal((await handler(request('/sounds/'+row.id,{method:'DELETE',headers:{authorization:'Bearer owner'}}))).status,200);
 const deletion=calls.find(c=>c.url.pathname==='/storage/v1/object/hub-audio'&&c.init.method==='DELETE');
 assert.deepEqual(JSON.parse(deletion.init.body).prefixes,[row.object_path]);
});
test('Owner bypasses upload quota and gets a server-derived crown',async()=>{
 const {handler,calls}=service({owner:true,quota:true});const result=await handler(upload()); assert.equal(result.status,201);
 assert.equal((await result.json()).uploaderIsOwner,true);assert.ok(!calls.some(c=>c.url.pathname.endsWith('hub_reserve_upload')));
});
test('server role endpoint does not trust username or display metadata',async()=>{
 const {handler}=service();const result=await handler(request('/account',{headers:{authorization:'Bearer regular'}}));
 assert.deepEqual(await result.json(),{isOwner:false});
 assert.equal((await handler(request('/admin/reports',{headers:{authorization:'Bearer regular'}}))).status,403);
});
test('public crown is derived from assigned user IDs only',async()=>{
 const {handler}=service({owner:true}); const result=await handler(request('/sounds'));assert.equal((await result.json()).sounds[0].uploaderIsOwner,true);
});

test('report quota errors remain 429 with a friendly client error code',async()=>{
 const {handler}=service({reportQuota:true});const result=await handler(request('/sounds/'+row.id+'/report',{method:'POST',headers:{authorization:'Bearer account'}}));
 assert.equal(result.status,429);assert.equal((await result.json()).error_code,'report_limit');
});
