const {app,dialog,clipboard}=require('electron');
const fs=require('node:fs');
const path=require('node:path');
const output=process.env.NEXUS_DESKTOP_TEST_REPORT;
if(!output||!path.isAbsolute(output))throw new Error('Owned desktop test report required');
let observed=false;
const nativeNotices=[];
const clipboardWrites=[];
clipboard.writeText=text=>clipboardWrites.push(text);
let phase='bootstrap';
function progress(value){phase=value;fs.writeFileSync(path.join(path.dirname(output),'client-progress.json'),JSON.stringify({phase,pid:process.pid}));}
dialog.showMessageBox=async(...args)=>{nativeNotices.push(args.at(-1).message);return {response:0,checkboxChecked:false};};
async function waitClient(window,expression,timeout=15000) {
  const end=Date.now()+timeout;
  do {
    const value=await window.webContents.executeJavaScript(expression);
    if(value)return value;
    await new Promise(resolve=>setTimeout(resolve,100));
  }while(Date.now()<end);
  throw new Error('Desktop lifecycle observation timeout: '+expression);
}
app.on('browser-window-created',(_event,window)=>{
  const businessResponses=[];
  const failedRequests=[],rendererMessages=[];
  window.webContents.on('console-message',(_event,...values)=>{
    const message=values[0]?.message??values[1];
    if(typeof message==='string')rendererMessages.push(message);
  });
  window.webContents.session.webRequest.onErrorOccurred(details=>failedRequests.push({path:new URL(details.url).pathname,error:details.error}));
  window.webContents.session.webRequest.onCompleted(details=>{
    if(details.webContentsId!==window.webContents.id||details.method!=='GET')return;
    const url=new URL(details.url);
    if(details.statusCode>=400)failedRequests.push({path:url.pathname,status:details.statusCode});
    if(url.hostname==='127.0.0.1'&&url.pathname.startsWith('/api/'))businessResponses.push({path:url.pathname,status:details.statusCode});
  });
  window.webContents.on('did-finish-load',async()=>{
    if(observed||!window.webContents.getURL().startsWith('http://127.0.0.1:'))return;
    observed=true;
    try{
      const deadline=Date.now()+10000;
      let page;
      do{
        page=await window.webContents.executeJavaScript(`({shell:!!document.querySelector('main'),navigation:['dashboard','dispatch','queues','scripts','users','history','plugins','settings'].filter(route=>document.querySelector('a[href="#/'+route+'"]')).length,bridge:window.nexusDesktop?.bootstrapProtocolVersion,origin:location.origin})`);
        if(page.shell&&page.navigation===8)break;
        await new Promise(resolve=>setTimeout(resolve,50));
      }while(Date.now()<deadline);
      if(!page.shell||page.navigation!==8||page.bridge!==1)throw new Error('Real Vue shell did not bootstrap');
      const initialPreferences=await window.webContents.executeJavaScript(`window.nexusDesktop.getClientPreferences()`);
      if(initialPreferences.locale!=='en-US'||initialPreferences.theme!=='light')throw new Error('Stored client preferences were not restored');
      const preferences=await window.webContents.executeJavaScript(`window.nexusDesktop.setClientPreferences({locale:'en-US',theme:'dark'}).then(()=>window.nexusDesktop.getClientPreferences())`);
      if(preferences.locale!=='en-US'||preferences.theme!=='dark')throw new Error('Typed client preference round trip failed');
      const pages=[];
      const requests={dashboard:'/api/status',dispatch:'/api/scripts',queues:'/api/queues',scripts:'/api/scripts',users:'/api/users/task-summaries',history:'/api/history/dates',plugins:'/api/plugins',settings:'/api/settings'};
      for(const [route,requestPath] of Object.entries(requests)) {
        const responseStart=route==='dashboard'?0:businessResponses.length;
        await window.webContents.executeJavaScript(`document.querySelector('a[href="#/${route}"]').click()`);
        const ready=Date.now()+5000;
        let observedPage;
        do {
          observedPage=await window.webContents.executeJavaScript(`({route:location.hash,active:document.querySelector('a[href="#/${route}"]')?.getAttribute('aria-current')==='page',text:document.querySelector('main')?.innerText??'',errors:Array.from(document.querySelectorAll('[role="alert"]')).map(x=>x.textContent)})`);
          observedPage.response=businessResponses.slice(responseStart).find(value=>value.path===requestPath&&value.status>=200&&value.status<300);
          if(observedPage.route==='#/'+route&&observedPage.active&&observedPage.response)break;
          await new Promise(resolve=>setTimeout(resolve,50));
        }while(Date.now()<ready);
        if(observedPage.route!=='#/'+route||!observedPage.active||!observedPage.response)throw new Error('Business route failed: '+route+' '+JSON.stringify(observedPage));
        pages.push(observedPage);
        if(process.env.NEXUS_DESKTOP_CAPTURE==='1') {
          await window.webContents.executeJavaScript(`new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))`);
          await new Promise(resolve=>setTimeout(resolve,100));
          fs.writeFileSync(path.join(path.dirname(output),route+'.png'),(await window.webContents.capturePage()).toPNG());
        }
      }
      const security=window.webContents.getLastWebPreferences();
      if(!security.sandbox||!security.contextIsolation||security.nodeIntegration)throw new Error('Renderer isolation failed');
      await window.webContents.executeJavaScript(`window.nexusDesktop.writeClipboardText('owned-desktop-copy')`);
      const invalidClipboard=await window.webContents.executeJavaScript(`Promise.all([null,42,'x'.repeat(65537),'界'.repeat(21846)].map(value=>window.nexusDesktop.writeClipboardText(value).then(()=>false,()=>true)))`);
      if(clipboardWrites.length!==1||clipboardWrites[0]!=='owned-desktop-copy'||invalidClipboard.some(value=>!value))throw new Error('Authenticated clipboard write contract failed');
      const beforeRestart=await window.webContents.executeJavaScript(`fetch('/api/status?view=identity').then(value=>value.json())`);
      progress('restart-draft');
      const rendererBefore=window.webContents.getOSProcessId();
      const draft=await window.webContents.executeJavaScript(`(()=>{const input=document.getElementById('st-retention');if(!input)throw new Error('Settings input missing');const value=String(Number(input.value)+1);input.value=value;input.dispatchEvent(new Event('input',{bubbles:true}));window.desktopDraftMarker='retained';return value;})()`);
      const restart=await window.webContents.executeJavaScript(`fetch('/api/settings/restart',{method:'POST'}).then(async response=>{if(!response.ok)throw new Error('Restart rejected');return response.json();})`);
      progress('restart-requested');
      const retained=await waitClient(window,`(()=>{const button=Array.from(document.querySelectorAll('button')).find(value=>value.textContent.trim()==='Discard changes and refresh');return button&&document.getElementById('st-retention')?.value===${JSON.stringify(draft)}&&window.desktopDraftMarker==='retained';})()`);
      if(!retained||window.webContents.getOSProcessId()!==rendererBefore)throw new Error('Host restart discarded the renderer or draft');
      progress('draft-retained');
      const afterRestart=await waitClient(window,`fetch('/api/status?view=identity').then(response=>response.json()).then(identity=>identity.ready&&identity.instanceId!==${JSON.stringify(beforeRestart.instanceId)}?identity:null).catch(()=>null)`);
      progress('discard-navigation');
      const refreshed=new Promise((resolve,reject)=>{const timer=setTimeout(()=>reject(new Error('Explicit discard did not load the accepted Host')),10000);window.webContents.once('did-finish-load',()=>{clearTimeout(timer);resolve();});});
      void window.webContents.executeJavaScript(`Array.from(document.querySelectorAll('button')).find(value=>value.textContent.trim()==='Discard changes and refresh').click()`).catch(()=>{});
      await refreshed;
      progress('navigation-finished');
      await waitClient(window,`location.hash==='#/settings'&&!!document.getElementById('st-retention')&&document.getElementById('st-retention').value!==${JSON.stringify(draft)}`);
      const rendererBeforeCrash=window.webContents.getOSProcessId();
      window.webContents.forcefullyCrashRenderer();
      progress('renderer-crashed');
      await new Promise(resolve=>setTimeout(resolve,250));
      await waitClient(window,`location.hash==='#/dashboard'&&!!document.querySelector('[data-testid="dashboard-state"]')`);
      const afterCrash=await window.webContents.executeJavaScript(`fetch('/api/status?view=identity').then(response=>response.json())`);
      if(afterCrash.instanceId!==afterRestart.instanceId||window.webContents.getOSProcessId()===rendererBeforeCrash||!nativeNotices.some(value=>value.includes('未保存的输入已丢失')))throw new Error('Renderer crash recovery changed Host or omitted the loss notice');
      window.close();
      if(window.isDestroyed()||window.isVisible())throw new Error('Window close did not hide');
      fs.writeFileSync(output,JSON.stringify({schemaVersion:1,status:'PASS',page,pages,preferences,security:{sandbox:security.sandbox,contextIsolation:security.contextIsolation,nodeIntegration:security.nodeIntegration},clipboard:{authenticatedWrite:true,invalidValuesRejected:true,systemClipboard:'substituted'},restart:{before:beforeRestart.instanceId,after:afterRestart.instanceId,handoff:restart,rendererRetained:true,draftRetained:true,explicitDiscard:true},rendererCrash:{hostPreserved:true,rendererReplaced:true,lossNotice:true},nativeDialogAcknowledgement:'substituted',closeHides:true,pid:process.pid}),{flag:'wx'});
    }catch(error){
      const observation=await window.webContents.executeJavaScript(`({url:location.href,text:document.body.innerText,retention:document.getElementById('st-retention')?.value,draftMarker:window.desktopDraftMarker})`).catch(()=>null);
      fs.writeFileSync(output,JSON.stringify({schemaVersion:1,status:'FAIL',phase,error:String(error),observation,nativeNotices,failedRequests,rendererMessages}),{flag:'wx'});
    }
  });
});
require('./main/index.js');
