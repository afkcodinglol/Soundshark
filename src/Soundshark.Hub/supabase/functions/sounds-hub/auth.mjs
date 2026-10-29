// Never return lookup emails. Only successful password authentication returns a
// session. Recovery sessions are held separately by the desktop until reset.
export function createAuthHandler(config, fetcher, rpc, json, HubError) {
  const base = new URL(config.url);
  async function auth(path, body) {
    return fetcher(new URL('/auth/v1/' + path, base), {
      method:'POST', headers:{apikey:config.anonKey,'content-type':'application/json'}, body:JSON.stringify(body)
    });
  }
  function check(result) {
    if (!result || result.error === 'invalid') throw new HubError(400,'No matching account. Check your username or email.');
    if (result.error === 'cooldown') throw new HubError(429,'Wait 60 seconds before sending another code.');
    if (result.error) throw new HubError(410,'Start password recovery again.');
    return result;
  }
  return async (route, request, bytes) => {
    let body;
    try { body = JSON.parse(new TextDecoder().decode(bytes)); } catch { throw new HubError(400,'Invalid request'); }
    if (!body || typeof body !== 'object') throw new HubError(400,'Invalid request');
    const identifier = typeof body.identifier === 'string' ? body.identifier.trim() : '';
    const challengeId = typeof body.challengeId === 'string' ? body.challengeId : '';
    if (!await rpc('hub_auth_throttle',{p_key:identifier.toLowerCase() || challengeId})) throw new HubError(429,'Too many requests. Please wait.');
    if (route === '/auth/check') {
      if (!identifier || identifier.length>254) throw new HubError(400,'Enter your username or email.');
      const account = await rpc('hub_auth_lookup',{p_identifier:identifier});
      return json({exists:!!account?.email});
    }
    if (route === '/auth/login') {
      if (!identifier || identifier.length>254 || typeof body.password!=='string' || body.password.length>128) throw new HubError(400,'Enter your username or email and password.');
      const account = await rpc('hub_auth_lookup',{p_identifier:identifier});
      if (!account?.email) throw new HubError(400,'Check your username or email and password.');
      const response = await auth('token?grant_type=password',{email:account.email,password:body.password});
      if (!response.ok) throw new HubError(response.status===429?429:400,'Check your username or email and password.');
      return json(await response.json());
    }
    if (route === '/auth/recover') {
      if (!identifier || identifier.length>254) throw new HubError(400,'Enter your username or email.');
      const challenge = check(await rpc('hub_recovery_begin',{p_identifier:identifier}));
      const response = await auth('recover',{email:challenge.email});
      if (!response.ok) throw new HubError(response.status===429?429:503,'Recovery email could not be sent. Please try again later.');
      return json({challengeId:challenge.challengeId});
    }
    if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(challengeId)) throw new HubError(400,'Invalid recovery request');
    if (route === '/auth/resend') {
      const challenge = check(await rpc('hub_recovery_step',{p_id:challengeId,p_action:'resend'}));
      const response = await auth('recover',{email:challenge.email});
      if (!response.ok) throw new HubError(response.status===429?429:503,'Recovery email could not be sent. Please try again later.');
      return json({sent:true});
    }
    if (route === '/auth/verify') {
      const challenge = check(await rpc('hub_recovery_step',{p_id:challengeId,p_action:'verify'}));
      const token = typeof body.token==='string'?body.token.trim():'';
      if (!/^\d{6,10}$/.test(token)) return json({verified:false,remaining:challenge.remaining});
      const response = await auth('verify',{email:challenge.email,token,type:'recovery'});
      if (response.status===400 || response.status===403 || response.status===422) return json({verified:false,remaining:challenge.remaining});
      if (!response.ok) throw new HubError(response.status===429?429:503,'Verification is unavailable. Please try again.');
      const session = await response.json();
      check(await rpc('hub_recovery_step',{p_id:challengeId,p_action:'consume'}));
      return json({verified:true,session});
    }
    throw new HubError(404,'Unknown account route');
  };
}
