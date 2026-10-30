import { inspectMp3, MAX_AUDIO_BYTES } from './audio.mjs';
import { createAuthHandler } from './auth.mjs';
import { createAdminHandler } from './admin.mjs';

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const countryCodes = new Set('AD,AE,AF,AG,AI,AL,AM,AO,AQ,AR,AS,AT,AU,AW,AX,AZ,BA,BB,BD,BE,BF,BG,BH,BI,BJ,BL,BM,BN,BO,BQ,BR,BS,BT,BV,BW,BY,BZ,CA,CC,CD,CF,CG,CH,CI,CK,CL,CM,CN,CO,CR,CU,CV,CW,CX,CY,CZ,DE,DJ,DK,DM,DO,DZ,EC,EE,EG,EH,ER,ES,ET,EU,FI,FJ,FK,FM,FO,FR,GA,GB,GB-ENG,GB-NIR,GB-SCT,GB-WLS,GD,GE,GF,GG,GH,GI,GL,GM,GN,GP,GQ,GR,GS,GT,GU,GW,GY,HK,HM,HN,HR,HT,HU,ID,IE,IL,IM,IN,IO,IQ,IR,IS,IT,JE,JM,JO,JP,KE,KG,KH,KI,KM,KN,KP,KR,KW,KY,KZ,LA,LB,LC,LI,LK,LR,LS,LT,LU,LV,LY,MA,MC,MD,ME,MF,MG,MH,MK,ML,MM,MN,MO,MP,MQ,MR,MS,MT,MU,MV,MW,MX,MY,MZ,NA,NC,NE,NF,NG,NI,NL,NO,NP,NR,NU,NZ,OM,PA,PE,PF,PG,PH,PK,PL,PM,PN,PR,PS,PT,PW,PY,QA,RE,RO,RS,RU,RW,SA,SB,SC,SD,SE,SG,SH,SI,SJ,SK,SL,SM,SN,SO,SR,SS,ST,SV,SX,SY,SZ,TC,TD,TF,TG,TH,TJ,TK,TL,TM,TN,TO,TR,TT,TV,TW,TZ,UA,UG,UM,US,UY,UZ,VA,VC,VE,VG,VI,VN,VU,WF,WS,XK,YE,YT,ZA,ZM,ZW'.split(','));
class HubError extends Error { constructor(status, message) { super(message); this.status = status; } }
const json = (body, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'content-type':'application/json', 'cache-control':'no-store', 'x-content-type-options':'nosniff' } });
const sound = (row, owners = []) => ({ id:row.id, title:row.title, uploader:row.uploader, duration:row.duration, bytes:row.bytes, sha256:row.sha256, ownerId:row.owner_id, uploaderIsOwner:owners.includes(row.owner_id), countryCode:row.country_code || null });
const fields = 'id,owner_id,title,uploader,duration,bytes,sha256,object_path,status,country_code';

async function boundedBody(request, maximum) {
  const reader = request.body?.getReader(); if (!reader) throw new HubError(400,'Missing audio');
  const chunks = []; let length = 0;
  try {
    while (true) { const result = await reader.read(); if (result.done) break; length += result.value.length;
      if (length > maximum) { await reader.cancel(); throw new HubError(413,'Upload too large'); } chunks.push(result.value); }
  } finally { reader.releaseLock(); }
  const bytes = new Uint8Array(length); let offset = 0;
  for (const chunk of chunks) { bytes.set(chunk,offset); offset += chunk.length; } return bytes;
}
export function createHandler(config, fetcher = fetch) {
  const base = new URL(config.url);
  async function api(path, options = {}) {
    const response = await fetcher(new URL(path,base), { ...options, headers: { apikey:config.serviceKey, authorization:'Bearer '+config.serviceKey, ...options.headers } });
    if (response.status===429) throw new HubError(429,'Six reports per 12 hours; please wait before reporting again');
    if (!response.ok) throw new HubError(503,'Sounds Hub is temporarily unavailable');
    return response;
  }
  async function rows(query) { return (await api('/rest/v1/hub_sounds?select='+fields+'&'+query)).json(); }
  async function countryPreference(id) {
    const query = new URLSearchParams({select:'country_code,show_country',user_id:'eq.'+id,limit:'1'});
    const values = await (await api('/rest/v1/hub_profiles?'+query)).json();
    return values[0] || {country_code:null,show_country:false};
  }
  const accountAuth = createAuthHandler(config, fetcher, async (name, body) => {
    const response = await api('/rest/v1/rpc/'+name,{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify(body)});
    return response.json();
  }, json, HubError);
  async function user(request) {
    const authorization = request.headers.get('authorization') || '';
    if (!/^Bearer \S+$/.test(authorization)) throw new HubError(401,'Sign in first');
    const response = await fetcher(new URL('/auth/v1/user',base), { headers:{ apikey:config.anonKey, authorization } });
    if (!response.ok) throw new HubError(401,'Sign in again');
    const value = await response.json();
    if (!uuid.test(value.id) || !value.email_confirmed_at) throw new HubError(403,'Verify your email first'); return value;
  }
  const administration = createAdminHandler(api, user, boundedBody, json, HubError);
  const isOwner = async id => (await (await api('/rest/v1/rpc/hub_is_owner', {method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({p_user_id:id})})).json()) === true;
  return async request => {
    try {
      const url = new URL(request.url); const prefix = '/sounds-hub'; const marker = url.pathname.indexOf(prefix);
      const route = marker < 0 ? '' : url.pathname.slice(marker+prefix.length);
      if (route === '/account' || route === '/issues' || route.startsWith('/admin/')) return await administration(route, request, url);
      if (request.method === 'POST' && route.startsWith('/auth/')) return await accountAuth(route, request, await boundedBody(request,4096));
      if (route === '/profile') {
        const account = await user(request);
        if (request.method === 'GET') {
          const preference = await countryPreference(account.id); return json({countryCode:preference.country_code,showCountry:preference.show_country===true});
        }
        if (request.method === 'PUT') {
          let value; try { value=JSON.parse(new TextDecoder('utf-8',{fatal:true}).decode(await boundedBody(request,1024))); } catch { throw new HubError(400,'Invalid country preference'); }
          const code=String(value.countryCode||'').toUpperCase();
          if (!countryCodes.has(code) || typeof value.showCountry!=='boolean') throw new HubError(400,'Invalid country preference');
          await api('/rest/v1/rpc/hub_set_country_preference',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({p_user_id:account.id,p_country_code:code,p_show_country:value.showCountry})});
          return json({countryCode:code,showCountry:value.showCountry});
        }
      }
      if (request.method === 'GET' && route === '/sounds') {
        const q = (url.searchParams.get('q') || '').trim(); const page = Number(url.searchParams.get('page') || 0);
        if (q.length > 80 || !Number.isInteger(page) || page < 0 || page > 250) throw new HubError(400,'Invalid search');
        const filters = new URLSearchParams({ order:'created_at.desc,id.desc', limit:'41', offset:String(page*40) });
        if (url.searchParams.get('mine') === '1') { const uploader = await user(request); filters.set('owner_id','eq.'+uploader.id); }
        else filters.set('status','eq.published');
        // Escape PostgREST wildcard characters; no raw query syntax is interpolated.
        if (q) filters.set('title','ilike.*'+q.replace(/[\\%_*]/g,'\\$&')+'*');
        const result = await rows(filters.toString()); const owners = (await (await api('/rest/v1/hub_owners?select=user_id')).json()).map(x=>x.user_id); return json({ sounds:result.slice(0,40).map(row=>sound(row,owners)), hasMore:result.length>40 });
      }
      const match = route.match(/^\/sounds\/([0-9a-f-]+)(\/audio|\/report)?$/i);
      if (match && uuid.test(match[1])) {
        const id = match[1], action = match[2] || '';
        if (request.method === 'GET' && action === '/audio') {
          const result = await rows('id=eq.'+id+'&status=eq.published&limit=1'); if (!result.length) throw new HubError(404,'Sound removed');
          const row = result[0];
          if (row.object_path !== row.owner_id+'/'+row.id+'.mp3') throw new HubError(503,'Invalid storage record');
          const audio = await api('/storage/v1/object/hub-audio/'+row.object_path);
          return new Response(audio.body,{headers:{'content-type':'audio/mpeg','content-length':String(row.bytes),'content-disposition':'attachment; filename="sound.mp3"','cache-control':'no-store','x-content-type-options':'nosniff'}});
        }
        if (request.method === 'DELETE' && action === '') {
          const uploader = await user(request); const admin = await isOwner(uploader.id);
          const result = await rows('id=eq.'+id+(admin?'':'&owner_id=eq.'+uploader.id)+'&limit=1');
          if (!result.length || (!admin && result[0].owner_id !== uploader.id)) throw new HubError(404,'Sound not found');
          const owner = result[0].owner_id;
          if (result[0].object_path !== owner+'/'+id+'.mp3') throw new HubError(503,'Invalid storage record');
          // Hide first; retain the owner record until storage confirms deletion.
          // A network failure remains retryable through My uploads.
          await api('/rest/v1/hub_sounds?id=eq.'+id+'&owner_id=eq.'+owner,{method:'PATCH',headers:{'content-type':'application/json'},body:JSON.stringify({status:'hidden'})});
          await api('/storage/v1/object/hub-audio',{method:'DELETE',headers:{'content-type':'application/json'},body:JSON.stringify({prefixes:[owner+'/'+id+'.mp3']})});
          await api('/rest/v1/hub_sounds?id=eq.'+id+'&owner_id=eq.'+owner,{method:'DELETE'});
          return json({ removed:true });
        }
        if (request.method === 'POST' && action === '/report') {
          const uploader = await user(request);
          const response = await api('/rest/v1/rpc/hub_submit_sound_report',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({p_sound_id:id,p_user_id:uploader.id})});
          if (!await response.json()) throw new HubError(400,'Report not accepted'); return json({ reported:true });
        }
      }
      if (request.method === 'POST' && route === '/upload') {
        const uploader = await user(request);
        const contentType = request.headers.get('content-type') || '';
        if (!contentType.startsWith('multipart/form-data;')) throw new HubError(400,'Expected an audio upload');
        const body = await boundedBody(request,MAX_AUDIO_BYTES+65536);
        const form = await new Response(body,{headers:{'content-type':contentType}}).formData();
        const title = String(form.get('title') || '').trim(), file = form.get('file');
        if (!title || title.length > 80 || /[\x00-\x1f]/.test(title) || form.get('rights') !== 'confirmed' || !file || typeof file === 'string') throw new HubError(400,'Invalid sound details');
        const bytes = new Uint8Array(await file.arrayBuffer()); let info;
        try { info = inspectMp3(bytes); } catch { throw new HubError(400,'Upload valid MP3 audio up to 60 seconds'); }
        const ownerAccount = await isOwner(uploader.id);
        const preference = await countryPreference(uploader.id);
        const reserved = ownerAccount ? null : await api('/rest/v1/rpc/hub_reserve_upload',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({p_user_id:uploader.id,p_bytes:bytes.length})});
        if (reserved && !await reserved.json()) throw new HubError(429,'Upload quota reached');
        const id = crypto.randomUUID(), objectPath = uploader.id+'/'+id+'.mp3';
        const sha256 = Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256',bytes)),b=>b.toString(16).padStart(2,'0')).join('');
        const display = String(uploader.user_metadata?.display_name || 'Soundshark user').replace(/[\x00-\x1f]/g,'').trim().slice(0,40) || 'Soundshark user';
        const row = {id,owner_id:uploader.id,title,uploader:display,duration:info.duration,bytes:bytes.length,sha256,object_path:objectPath,country_code:preference.show_country?preference.country_code:null};
        await api('/storage/v1/object/hub-audio/'+objectPath,{method:'POST',headers:{'content-type':'audio/mpeg','x-upsert':'false'},body:bytes});
        try { await api('/rest/v1/hub_sounds',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify(row)}); }
        catch(error) { try { await api('/storage/v1/object/hub-audio',{method:'DELETE',headers:{'content-type':'application/json'},body:JSON.stringify({prefixes:[objectPath]})}); } catch { console.error('Hub upload cleanup required',id); } throw error; }
        return json(sound(row,ownerAccount?[uploader.id]:[]),201);
      }
      return json({message:'Not found'},404);
    } catch (error) {
      if (error instanceof HubError) return json({message:error.message,...(error.status===429&&error.message.startsWith('Six reports')?{error_code:'report_limit'}:{})},error.status);
      // Do not expose tokens, private service responses or uploader emails.
      console.error('Sounds Hub request failed'); return json({message:'Sounds Hub is temporarily unavailable'},503);
    }
  };
}
