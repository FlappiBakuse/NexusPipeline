const {app,BrowserWindow,protocol,session}=require('electron');
const {EventEmitter}=require('node:events');
const {BrowserLoginWindows}=require('./main/browser-login.js');
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const output=process.env.NEXUS_BROWSER_LOGIN_REPORT;
if(!output||!path.isAbsolute(output))throw new Error('Owned report required');
app.setPath('userData',path.join(path.dirname(output),'client-data'));
app.on('window-all-closed',()=>{});
protocol.registerSchemesAsPrivileged([{scheme:'nxp-desktop',privileges:{standard:true,secure:true,supportFetchAPI:true}}]);
const until=async test=>{const end=Date.now()+10000;while(Date.now()<end){if(await test())return;await new Promise(r=>setTimeout(r,25));}throw new Error('Bounded browser observation failed');};
class Supervisor extends EventEmitter {
  captures=[];terminals=[];
  async request(type,data){
    if(type==='browser.complete'){this.captures.push(data.capture);return {success:true};}
    this.terminals.push({type,...data});return {accepted:true};
  }
}
async function run(){
  await app.whenReady();const supervisor=new Supervisor(),windows=new BrowserLoginWindows(supervisor,path.join(__dirname,'preload/login.js'));
  protocol.handle('nxp-desktop',request=>{const url=new URL(request.url),html=windows.document(url.pathname.slice(1));return new Response(html||'',{status:html?200:404,headers:{'Content-Type':'text/html'}});});
  const cases=[];
  try {
    for(const [index,completed] of [true,false].entries()) {
      const id=String(index+1).padStart(32,'0'),partition='nxp-login-'+id,isolated=session.fromPartition(partition,{cache:false});
      assert.equal((await isolated.cookies.get({})).length,0);
      isolated.protocol.handle('https',()=>new Response(`<html><body><script>document.cookie='selected=synthetic-secret; path=/; SameSite=Strict; Secure';document.cookie='ignored=private-unselected; path=/; SameSite=Strict; Secure';localStorage.setItem('allowed',JSON.stringify({token:'synthetic-storage'}));localStorage.setItem('ignored','unselected-storage');</script><a href="https://outside.invalid/">outside</a></body></html>`,{headers:{'Content-Type':'text/html'}}));
      const flow={id:'fixture',title:'Controlled login',flowVersion:'1',startUri:'https://login.nxp.test/',navigationOrigins:['https://login.nxp.test'],popupOrigins:[],cookies:[{domain:'login.nxp.test',path:'/',name:'selected'}],storage:[{origin:'https://login.nxp.test',storage:'localStorage',key:'allowed',jsonPath:['token']}],ready:true};
      const descriptor={operationId:id,flow,expiresAt:new Date(Date.now()+900000).toISOString()};
      await windows.open(descriptor);
      const entry=windows.windows.get(id);await until(()=>entry.view.webContents.getURL()===flow.startUri&&!entry.view.webContents.isLoading());
      assert.equal(await entry.view.webContents.executeJavaScript(`typeof require==='undefined'&&typeof window.nexusDesktop==='undefined'&&typeof window.loginShell==='undefined'`),true);
      const prefs=entry.view.webContents.getLastWebPreferences();assert.equal(prefs.sandbox,true);assert.equal(prefs.contextIsolation,true);assert.equal(prefs.nodeIntegration,false);assert.ok(!prefs.preload);
      const count=BrowserWindow.getAllWindows().length;await windows.open(descriptor);assert.equal(BrowserWindow.getAllWindows().length,count);
      await entry.view.webContents.executeJavaScript(`document.querySelector('a').click();window.open('https://outside.invalid/');`);
      await new Promise(r=>setTimeout(r,100));assert.equal(entry.view.webContents.getURL(),flow.startUri);assert.equal(BrowserWindow.getAllWindows().length,count);
      const trusted=entry.window.webContents,external=entry.view.webContents;
      await trusted.executeJavaScript(`void window.loginShell.command(${JSON.stringify(id)},${JSON.stringify(completed?'complete':'cancel')}).catch(()=>{})`);
      await until(()=>!windows.windows.has(id));
      assert.equal(entry.window.isDestroyed(),true);assert.equal(external.isDestroyed(),true);assert.equal((await isolated.cookies.get({})).length,0);
      assert.equal(supervisor.terminals.at(-1).type,completed?'browser.cleaned':'browser.cancel');
      if(completed){assert.equal(supervisor.terminals.at(-1).cleaned,true);assert.deepEqual(supervisor.captures.at(-1).cookies,[{domain:'login.nxp.test',path:'/',name:'selected',value:'synthetic-secret'}]);assert.deepEqual(supervisor.captures.at(-1).storage,[{origin:'https://login.nxp.test',storage:'localStorage',key:'allowed',value:'synthetic-storage'}]);}
      cases.push({name:completed?'success':'cancel',isolated:true,externalBridgeAbsent:true,unapprovedNavigationBlocked:true,unapprovedPopupBlocked:true,selectedCaptureOnly:completed,windowDestroyed:true,cookiesCleared:true});
      await isolated.protocol.unhandle('https');
    }
    await windows.stop();assert.equal(BrowserWindow.getAllWindows().length,0);
    fs.writeFileSync(output,JSON.stringify({status:'PASS',classification:'CONTROLLED_ELECTRON',electron:process.versions.electron,cases,realPlatformLogin:'not_run'},null,2),{flag:'wx'});
  }catch(error){fs.writeFileSync(output,JSON.stringify({status:'FAIL',errorType:error.name,message:error.message,realPlatformLogin:'not_run'},null,2),{flag:'wx'});process.exitCode=1;}
  finally{await windows.stop();app.quit();}
}
void run().catch(error=>{fs.writeFileSync(output,JSON.stringify({status:'FAIL',errorType:error.name,message:error.message}));app.exit(1);});
