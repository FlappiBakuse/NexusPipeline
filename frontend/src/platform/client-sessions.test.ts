import {beforeEach,describe,expect,it,vi} from 'vitest';
const mocks=vi.hoisted(()=>({api:vi.fn(),auth:'authority',bridge:null as null|{getClientSessionToken:()=>Promise<string>},onTraffic:vi.fn()}));
vi.mock('./api',()=>({api:mocks.api,readAuthToken:()=>mocks.auth}));
vi.mock('./desktop',()=>({desktopBridge:()=>mocks.bridge}));
vi.mock('./service-traffic',()=>({onServiceTrafficChanged:mocks.onTraffic}));
const web={hostSessionId:'host',clientSessionId:'tab',clientKind:'web',nativeBrowserAvailable:false};
describe('client session isolation',()=>{
  beforeEach(()=>{vi.resetModules();mocks.api.mockReset();mocks.auth='authority';mocks.bridge=null;sessionStorage.clear();});
  it('does not reuse a duplicated tabs token or persist disclosure authority',async()=>{
    sessionStorage.setItem('nxp-client-session','other-tab-token');
    mocks.api.mockResolvedValue({token:'new-tab-token',session:web});
    const session=await import('./client-sessions');
    await expect(session.getClientSession()).resolves.toEqual(web);
    expect(mocks.api).toHaveBeenCalledWith('POST','/api/client-sessions',{});
    expect(session.clientSessionHeaders()).toEqual({'X-Nxp-Client-Session':'new-tab-token'});
    expect(sessionStorage.getItem('nxp-client-session')).toBeNull();
  });
  it('rejects a late issuance after session invalidation without restoring its token',async()=>{
    let resolve!:(value:unknown)=>void;mocks.api.mockReturnValueOnce(new Promise(done=>{resolve=done;}));
    const session=await import('./client-sessions'),pending=session.getClientSession();
    const rejected=expect(pending).rejects.toThrow('client_session_changed');
    session.invalidateClientSession();resolve({token:'stale-token',session:web});await rejected;
    expect(session.clientSessionHeaders()).toEqual({});
    mocks.api.mockResolvedValueOnce({token:'fresh-token',session:web});await session.getClientSession();
    expect(session.clientSessionHeaders()).toEqual({'X-Nxp-Client-Session':'fresh-token'});
  });
  it('reacquires native authority only through the bridge after server revocation',async()=>{
    const issue=vi.fn().mockResolvedValueOnce('revoked').mockResolvedValueOnce('replacement');mocks.bridge={getClientSessionToken:issue};
    mocks.api.mockRejectedValueOnce({code:'client_session_required'}).mockResolvedValueOnce({...web,clientKind:'desktop',nativeBrowserAvailable:true});
    const session=await import('./client-sessions');await session.getClientSession();
    expect(issue).toHaveBeenCalledTimes(2);expect(mocks.api.mock.calls.every(call=>call[0]==='GET')).toBe(true);
    expect(session.clientSessionHeaders()).toEqual({'X-Nxp-Client-Session':'replacement'});
  });
});
