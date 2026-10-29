// All role decisions use the private, database-controlled owner assignment.
export function createAdminHandler(api, user, boundedBody, json, HubError) {
  async function rpc(name, body) {
    return (await api('/rest/v1/rpc/'+name, {method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify(body)})).json();
  }
  async function body(request, maximum) {
    try { return JSON.parse(new TextDecoder('utf-8',{fatal:true}).decode(await boundedBody(request,maximum))); }
    catch (error) { if (error instanceof HubError) throw error; throw new HubError(400,'Invalid report'); }
  }
  return async (route, request, url) => {
    const account = await user(request);
    const owner = await rpc('hub_is_owner',{p_user_id:account.id}) === true;
    if (route === '/account' && request.method === 'GET') return json({isOwner:owner});
    if (route === '/issues' && request.method === 'POST') {
      const value = await body(request,3*1024*1024);
      const message = String(value.message || '').trim(), files = value.files || [], kind=value.kind||'issue';
      if(!['issue','suggestion'].includes(kind)) throw new HubError(400,'Invalid submission category');
      if (message.length < 10 || message.length > 8000 || !Array.isArray(files) || files.length > 5)
        throw new HubError(400,'Enter 10–8000 characters and attach up to five log files');
      const names = new Set();
      for (const file of files) {
        if (typeof file.name !== 'string' || !/^[^\\/\x00-\x1f]{1,120}\.(log|txt|json)$/i.test(file.name) || names.has(file.name.toLowerCase()) ||
            typeof file.text !== 'string' || file.text.includes('\0') || new TextEncoder().encode(file.text).length > 512*1024)
          throw new HubError(400,'Choose distinct text log files, up to 512 KB each');
        names.add(file.name.toLowerCase());
      }
      const id = await rpc('hub_create_issue',{p_user_id:account.id,p_body:message,p_files:files,p_kind:kind});
      if (!id) return json({error_code:kind==='suggestion'?'suggestion_limit':'report_limit'},429);
      return json({id},201);
    }
    if (!route.startsWith('/admin/') || !owner) throw new HubError(403,'Owner access required');
    if (route === '/admin/reports' && request.method === 'GET') {
      const status = url.searchParams.get('status') || 'open';
      const page = Number(url.searchParams.get('page') || '0');
      if (!['open','resolved','unfixable'].includes(status) || !Number.isInteger(page) || page < 0 || page > 10000) throw new HubError(400,'Invalid report filter');
      const query = new URLSearchParams({select:'id,user_id,username,kind,body,sound_id,status,created_at,updated_at',status:'eq.'+status,order:'created_at.desc,id.desc',limit:'41',offset:String(page*40)});
      const rows = await (await api('/rest/v1/hub_support_reports?'+query)).json();
      return json({reports:rows.slice(0,40),hasMore:rows.length>40});
    }
    const match = route.match(/^\/admin\/reports\/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$/i);
    if (match && request.method === 'GET') {
      const rows = await (await api('/rest/v1/hub_support_reports?select=*&id=eq.'+match[1]+'&limit=1')).json();
      if (!rows.length) throw new HubError(404,'Report not found');
      return json(rows[0]);
    }
    if (match && request.method === 'PATCH') {
      const value = await body(request,4096);
      if (!['open','resolved','unfixable'].includes(value.status)) throw new HubError(400,'Invalid report status');
      const response = await api('/rest/v1/hub_support_reports?id=eq.'+match[1],{method:'PATCH',headers:{'content-type':'application/json',prefer:'return=representation'},body:JSON.stringify({status:value.status,updated_at:new Date().toISOString()})});
      if (!(await response.json()).length) throw new HubError(404,'Report not found');
      return json({updated:true});
    }
    throw new HubError(404,'Not found');
  };
}
