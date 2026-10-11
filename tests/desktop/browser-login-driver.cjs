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
  protocol.handle('nxp-desktop',request=>{const url=new URL(request.url),html=windows.document(url.pathname.slice(1));return new Response(html||'',{status:html?200:404,headers:{'Content-Type':'text/html; charset=utf-8'}});});
  const cases=[];let activePlatform='capture';const bootstrapObservations=[];
  try {
    for(const [index,completed] of [true,false].entries()) {
      const id=String(index+1).padStart(32,'0'),partition='nxp-login-'+id,isolated=session.fromPartition(partition,{cache:false});
      assert.equal((await isolated.cookies.get({})).length,0);
      let pageLoads=0;
      isolated.protocol.handle('https',request=>{
        if(new URL(request.url).pathname==='/token')return new Response(JSON.stringify({data:{content:'synthetic-passport',ignored:'unselected'}}),{headers:{'Content-Type':'application/json'}});
        const seed=++pageLoads===1?`document.cookie='selected=synthetic-secret; path=/; SameSite=Strict; Secure';document.cookie='ignored=private-unselected; path=/; SameSite=Strict; Secure';localStorage.setItem('allowed',JSON.stringify({token:'synthetic-storage'}));localStorage.setItem('ignored','unselected-storage');`:'';
        return new Response(`<html><body><script>${seed}</script><a href="https://outside.invalid/">outside</a></body></html>`,{headers:{'Content-Type':'text/html; charset=utf-8'}});
      });
      const flow={id:'fixture',title:'Controlled login',flowVersion:'1',startUri:'https://login.nxp.test/',navigationOrigins:['https://login.nxp.test'],popupOrigins:[],cookies:[{domain:'login.nxp.test',path:'/',name:'selected'}],storage:[{origin:'https://login.nxp.test',storage:'localStorage',key:'allowed',jsonPath:['token']}],jsonRequests:[{id:'passport',origin:'https://login.nxp.test',uri:'https://login.nxp.test/token',method:'GET',jsonPath:['data','content']}],ready:true};
      const descriptor={operationId:id,flow,expiresAt:new Date(Date.now()+900000).toISOString()};
      await windows.open(descriptor);
      const entry=windows.windows.get(id);await until(()=>entry.view.webContents.getURL()===flow.startUri&&!entry.view.webContents.isLoading());
      const refreshed=new Promise(resolve=>entry.view.webContents.once('did-finish-load',resolve));
      await entry.view.webContents.executeJavaScript('location.reload()');await refreshed;
      assert.equal(pageLoads,2);
      assert.equal(await entry.view.webContents.executeJavaScript("localStorage.getItem('allowed')"),JSON.stringify({token:'synthetic-storage'}));
      assert.equal((await isolated.cookies.get({name:'selected'}))[0].value,'synthetic-secret');
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
      if(completed)assert.deepEqual(supervisor.captures.at(-1).jsonValues,[{id:'passport',value:'synthetic-passport'}]);
      cases.push({name:completed?'success':'cancel',isolated:true,loginRefreshRetainsSession:true,externalBridgeAbsent:true,unapprovedNavigationBlocked:true,unapprovedPopupBlocked:true,selectedCaptureOnly:completed,windowDestroyed:true,cookiesCleared:true});
      await isolated.protocol.unhandle('https');
    }
    const scripts=JSON.parse(fs.readFileSync(path.join(__dirname,'page-scripts.json')));
    const headers={cn:'<a class="header__avatar" href="?id=undefined">avatar</a>',kuro:'<div class="right-section"><button class="personal-center-btn"></button></div>',skland:'<div class="header-right"><div class="header-button">登录</div></div>',skport:'<header><div class="header-right"><div class="Header__AvatarWrapper-fixture"><img src=""></div></div></header>'};
    const selectors={cn:'a.header__avatar',kuro:'.personal-center-btn',skland:'.header-button',skport:'[class*=Header__AvatarWrapper]'};
    for(const [index,platform] of ['cn','kuro','skland','skport'].entries()){
      activePlatform=platform;
      const id=String(index+10).padStart(32,'0'),isolated=session.fromPartition('nxp-login-'+id,{cache:false});
      isolated.protocol.handle('https',()=>new Response(`<html><body><script>window.clicks=0;if(['cn','skport'].includes(${JSON.stringify(platform)}))setTimeout(()=>history.replaceState(null,'',location.href),50);setTimeout(()=>{document.body.insertAdjacentHTML('beforeend',${JSON.stringify(headers[platform])});document.querySelector(${JSON.stringify(selectors[platform])}).onclick=event=>{event.preventDefault();window.clicks++;if(${JSON.stringify(platform)}==='skport'){document.body.insertAdjacentHTML('beforeend','<div class="tooltip-modal"><div class="HAvatar__PopoverWrapper"><div class="HAvatar__ItemList"><div class="HAvatar__ItemBlock">Login</div></div></div></div>');document.querySelector('.HAvatar__ItemBlock').onclick=()=>{window.loginClicks=(window.loginClicks||0)+1;document.body.insertAdjacentHTML('beforeend','<div role="dialog">login</div>');};}else document.body.insertAdjacentHTML('beforeend','<div role="dialog">login</div>');};},100);</script></body></html>`,{headers:{'Content-Type':'text/html; charset=utf-8'}}));
      const flow={id:'fixture',title:platform,flowVersion:'1',startUri:'https://login.nxp.test/',navigationOrigins:['https://login.nxp.test'],popupOrigins:[],cookies:[{domain:'login.nxp.test',path:'/',name:'selected'}],storage:[],ready:true,pageBootstrap:{origin:'https://login.nxp.test',path:'/',script:scripts[platform]}};
      await windows.open({operationId:id,flow,expiresAt:new Date(Date.now()+900000).toISOString()});
      const entry=windows.windows.get(id);
      const page=entry.bootstrap.page;
      entry.bootstrap.page=()=>{const current=page();if(!current)return current;return {...current,run:async code=>{try{const result=await current.run(code);bootstrapObservations.push({platform,result});return result;}catch(error){bootstrapObservations.push({platform,error:error.message});throw error;}}};};
      await until(()=>entry.view.webContents.executeJavaScript("window.clicks===1&&!!document.querySelector('[role=dialog]')"));
      entry.view.webContents.emit('did-finish-load');await new Promise(resolve=>setTimeout(resolve,300));
      assert.equal(await entry.view.webContents.executeJavaScript('window.clicks'),1);
      const reloaded=new Promise(resolve=>entry.view.webContents.once('did-finish-load',resolve));entry.view.webContents.reload();await reloaded;await until(()=>entry.view.webContents.executeJavaScript("window.clicks===1&&!!document.querySelector('[role=dialog]')"));if(platform==='skport')assert.equal(await entry.view.webContents.executeJavaScript('window.loginClicks'),1);
      assert.equal(await entry.view.webContents.executeJavaScript(`(${scripts[platform]})()`),'already-open');
      await entry.view.webContents.executeJavaScript(`document.querySelector('[role=dialog]').remove();const avatar=document.querySelector(${JSON.stringify(selectors[platform])});if(${JSON.stringify(platform)}==='cn')avatar.href='?id=123';else if(${JSON.stringify(platform)}==='skport')avatar.querySelector('img').setAttribute('src','https://login.nxp.test/avatar.png');else avatar.textContent='FixtureUser';`);
      assert.equal(await entry.view.webContents.executeJavaScriptInIsolatedWorld(1001,[{code:`(${scripts[platform]})()`}]),'already-authenticated');
      assert.equal(await entry.view.webContents.executeJavaScript('window.clicks'),1);
      if(platform==='skport'){
        await entry.view.webContents.executeJavaScript("document.querySelectorAll('.tooltip-modal').forEach(element=>element.remove());document.querySelector('[class*=Header__AvatarWrapper] img').removeAttribute('src');window.menuClicks=0;document.body.insertAdjacentHTML('beforeend','<div class=\"tooltip-modal\"><div class=\"HAvatar__PopoverWrapper\"><div class=\"HAvatar__ItemList\"><div class=\"HAvatar__ItemBlock\">Profile</div></div><div class=\"HAvatar__ItemList\"><div class=\"HAvatar__ItemBlock\">Logout</div></div></div></div>');document.querySelectorAll('.HAvatar__ItemBlock').forEach(element=>element.onclick=()=>window.menuClicks++);");
        for(const text of ['Logout','Log out','Sign out','退出登录','退出登錄','退出','登出','注销','註銷','ログアウト','로그아웃']){
          await entry.view.webContents.executeJavaScript(`document.querySelector('.HAvatar__ItemList:last-child .HAvatar__ItemBlock').textContent=${JSON.stringify(text)}`);
          assert.equal(await entry.view.webContents.executeJavaScriptInIsolatedWorld(1001,[{code:`(${scripts[platform]})()`}]),'already-authenticated');
        }
        await entry.view.webContents.executeJavaScript("document.querySelector('.HAvatar__ItemList:last-child .HAvatar__ItemBlock').textContent='Unknown action'");
        assert.equal(await entry.view.webContents.executeJavaScriptInIsolatedWorld(1001,[{code:`(${scripts[platform]})()`}]),'waiting');
        assert.equal(await entry.view.webContents.executeJavaScript('window.menuClicks'),0);
        for(const text of ['Login','Log in','Sign in','登录','登錄','登入','ログイン','로그인']){
          await entry.view.webContents.executeJavaScript(`document.querySelector('.HAvatar__ItemList:last-child .HAvatar__ItemBlock').textContent=${JSON.stringify(text)}`);
          await entry.view.webContents.executeJavaScriptInIsolatedWorld(1001,[{code:'globalThis.__nxpSkportLoginClicked=false'}]);
          assert.equal(await entry.view.webContents.executeJavaScriptInIsolatedWorld(1001,[{code:`(${scripts[platform]})()`}]),'clicked');
          assert.equal(await entry.view.webContents.executeJavaScriptInIsolatedWorld(1001,[{code:`(${scripts[platform]})()`}]),'waiting');
        }
        assert.equal(await entry.view.webContents.executeJavaScript('window.menuClicks'),8);
        await entry.view.webContents.executeJavaScript("document.querySelectorAll('.HAvatar__ItemBlock').forEach(element=>element.textContent='Login')");
        await entry.view.webContents.executeJavaScriptInIsolatedWorld(1001,[{code:'globalThis.__nxpSkportLoginClicked=false'}]);
        assert.equal(await entry.view.webContents.executeJavaScriptInIsolatedWorld(1001,[{code:`(${scripts[platform]})()`}]),'waiting');
        assert.equal(await entry.view.webContents.executeJavaScript('window.menuClicks'),8);
      }
      await entry.view.webContents.executeJavaScript(`document.querySelector(${JSON.stringify(selectors[platform])}).remove()`);
      assert.equal(await entry.view.webContents.executeJavaScriptInIsolatedWorld(1001,[{code:`(${scripts[platform]})()`}]),'waiting');
      entry.window.close();await until(()=>!windows.windows.has(id));
      await isolated.protocol.unhandle('https');cases.push({name:platform,pluginScript:true,delayedEntry:true,sameAddressHistory:platform==='cn'||platform==='skport',oneClickPerStepPerLoad:true,steps:platform==='skport'?2:1,refresh:true,existingDialogSkipped:true,authenticatedSkipped:true,missingEntryWaits:true,windowClose:true,...(platform==='skport'?{unloadedAvatarLogoutSkipped:true,unknownMenuSkipped:true,ambiguousLoginSkipped:true,localizedLoginChecked:true}:{})});
    }
    await windows.stop();assert.equal(BrowserWindow.getAllWindows().length,0);
    fs.writeFileSync(output,JSON.stringify({status:'PASS',classification:'CONTROLLED_ELECTRON',electron:process.versions.electron,cases,realPlatformLogin:'not_run'},null,2),{flag:'wx'});
  }catch(error){const pages=[];for(const entry of windows.windows.values()){pages.push({url:entry.view.webContents.getURL(),html:await entry.view.webContents.executeJavaScript('document.body.innerHTML').catch(()=>null)});}fs.writeFileSync(output,JSON.stringify({status:'FAIL',activePlatform,pages,bootstrapObservations,errorType:error.name,message:error.message,stack:error.stack,realPlatformLogin:'not_run'},null,2),{flag:'wx'});process.exitCode=1;}
  finally{await windows.stop();app.quit();}
}
void run().catch(error=>{fs.writeFileSync(output,JSON.stringify({status:'FAIL',errorType:error.name,message:error.message}));app.exit(1);});
