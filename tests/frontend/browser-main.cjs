'use strict';
const { app, BrowserWindow } = require('electron');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const crypto = require('node:crypto');

const input = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const runtime = fs.realpathSync(input.runtime);
const output = path.dirname(fs.realpathSync(process.argv[2]));
if (path.dirname(path.resolve(input.report)) !== output) throw new Error('Report escaped owned run');
app.setPath('userData', path.join(output, 'profile'));
app.setPath('crashDumps', path.join(output, 'crashes'));
const manifest = new Map(input.assets.map(record => ['/' + record.path, record]));
for (const [url, record] of manifest) {
  const file = path.resolve(runtime, record.path);
  if (!file.startsWith(runtime + path.sep) || fs.realpathSync(file) !== file) throw new Error('Asset escaped runtime');
  const raw = fs.readFileSync(file);
  if (crypto.createHash('sha256').update(raw).digest('hex') !== record.sha256) throw new Error('Asset changed: ' + url);
}
let window;
let server;
let completed = false;
const consoleErrors = [];
const stages = [];
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
function save(value) {
  if (completed) return;
  completed = true;
  fs.writeFileSync(input.report, JSON.stringify(value, null, 2) + '\n', { flag: 'wx' });
}
async function script(expression) {
  stages.push(expression);
  const result = await window.webContents.executeJavaScript('(async()=>{try{return {ok:true,value:await (' + expression
    + ')}}catch(error){return {ok:false,message:error.message,stack:error.stack}}})()');
  if (!result.ok) throw new Error(result.message + '\n' + result.stack);
  return result.value;
}
async function key(keyCode) {
  window.webContents.focus();
  await script('new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))');
  const value = keyCode === 'Space' ? { key: ' ', code: 'Space', windowsVirtualKeyCode: 32, nativeVirtualKeyCode: 32 }
    : { key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27, nativeVirtualKeyCode: 27 };
  await window.webContents.debugger.sendCommand('Input.dispatchKeyEvent', { type: 'keyDown', ...value,
    ...(keyCode === 'Space' ? { text: ' ', unmodifiedText: ' ' } : {}) });
  await window.webContents.debugger.sendCommand('Input.dispatchKeyEvent', { type: 'keyUp', ...value });
  await delay(40);
}
async function checkAmbientMotion() {
  const media = value => window.webContents.debugger.sendCommand('Emulation.setEmulatedMedia',
    { features: [{ name: 'prefers-reduced-motion', value }] });
  const draws = () => script('globalThis.ambientMotion.draws');
  const progressed = async (label, baseline) => {
    const deadline = Date.now() + 3000;
    while (Date.now() < deadline) {
      const value = await draws();
      if (value > baseline + 4) return value;
      await delay(30);
    }
    throw new Error(label + ' did not draw within 3 seconds');
  };
  const stable = async label => {
    const first = await draws(); await delay(110);
    if (await draws() !== first) throw new Error(label + ' scheduled another ambient frame');
    return first;
  };
  await media('reduce');
  await script(`(async()=>{
    const add = MediaQueryList.prototype.addEventListener, remove = MediaQueryList.prototype.removeEventListener;
    const draw = CanvasRenderingContext2D.prototype.clearRect, fill = CanvasRenderingContext2D.prototype.fill;
    const state = { draws: 0, dots: 0, added: 0, removed: 0, add, remove, draw, fill };
    globalThis.ambientMotion = state;
    MediaQueryList.prototype.addEventListener = function(type, ...args) {
      if (type === 'change' && this.media === '(prefers-reduced-motion: reduce)') state.added++;
      return add.call(this, type, ...args);
    };
    MediaQueryList.prototype.removeEventListener = function(type, ...args) {
      if (type === 'change' && this.media === '(prefers-reduced-motion: reduce)') state.removed++;
      return remove.call(this, type, ...args);
    };
    CanvasRenderingContext2D.prototype.clearRect = function(...args) {
      if (this.canvas.classList.contains('ambient-layer')) state.draws++;
      return draw.apply(this, args);
    };
    CanvasRenderingContext2D.prototype.fill = function(...args) {
      if (this.canvas.classList.contains('ambient-layer')) state.dots++;
      return fill.apply(this, args);
    };
    const { createApp, h, ref, nextTick } = await import('/vue.js');
    const { default: AmbientLayer } = await import('/apps/web/src/app/AmbientLayer.js');
    const visible = ref(true), holder = document.createElement('div');
    holder.style.cssText = 'width:320px;height:200px'; document.body.append(holder);
    const app = createApp({ setup: () => () => h(AmbientLayer, { visible: visible.value }) });
    app.mount(holder); Object.assign(state, { visible, holder, app, nextTick });
  })()`);
  try {
    await progressed('Initial reduced motion', -4);
    const reduced = await stable('Reduced motion');
    const expectedParticles = await script('innerWidth < 640 ? 36 : innerWidth < 1000 ? 56 : 80');
    if (reduced < 1 || await script('globalThis.ambientMotion.dots') !== reduced * expectedParticles)
      throw new Error('Reduced motion omitted its viewport particle render');
    await media('no-preference');
    const resumed = await progressed('Normal motion', reduced);
    await media('reduce'); await delay(180);
    const stopped = await stable('Normal to reduced motion');
    await script('(async()=>{globalThis.ambientMotion.visible.value=false; await globalThis.ambientMotion.nextTick(); return true})()');
    await delay(80); const hidden = await stable('Hidden ambient');
    await media('no-preference'); await delay(180);
    if (await draws() !== hidden) throw new Error('Hidden preference change scheduled an ambient frame');
    await script('(async()=>{globalThis.ambientMotion.visible.value=true; await globalThis.ambientMotion.nextTick(); return true})()');
    const shown = await progressed('Visible ambient', hidden);
    const unmounted = await script(`(()=>{ const state=globalThis.ambientMotion;
      state.app.unmount(); state.unmounted=true; state.holder.remove();
      return { draws: state.draws, added: state.added, removed: state.removed }; })()`);
    await media('reduce'); await media('no-preference'); await delay(180);
    if (await draws() !== unmounted.draws || unmounted.added !== 1 || unmounted.removed !== 1)
      throw new Error('Unmount kept ambient frames or its media listener');
    return { reduced, resumed, stopped, hidden, shown, unmounted };
  } finally {
    await script(`(()=>{ const state=globalThis.ambientMotion;
      if (state) { if (!state.unmounted) state.app?.unmount(); state.holder?.remove();
        MediaQueryList.prototype.addEventListener=state.add; MediaQueryList.prototype.removeEventListener=state.remove;
        CanvasRenderingContext2D.prototype.clearRect=state.draw; CanvasRenderingContext2D.prototype.fill=state.fill;
        delete globalThis.ambientMotion; } })()`);
    await media('no-preference');
  }
}
async function checkImageOwnership() {
  return await script(`(async () => {
    const { createApp, h, ref, nextTick } = await import('/vue.js');
    const { default: MediaImage } = await import('/apps/web/src/components/MediaImage.js');
    if (typeof ImageDecoder !== 'function' || !(await ImageDecoder.isTypeSupported('image/png')))
      throw new Error('Pinned desktop does not expose its explicit PNG decoder');
    const canvas=document.createElement('canvas'); canvas.width=640; canvas.height=480;
    const context=canvas.getContext('2d'); context.fillStyle='rgb(24,112,208)'; context.fillRect(0,0,640,480);
    const blob=await new Promise(resolve=>canvas.toBlob(resolve,'image/png'));
    const url=URL.createObjectURL(blob), holder=document.createElement('div');
    holder.style.cssText='position:fixed;left:20px;top:20px;width:320px;height:200px;z-index:10000';
    document.body.append(holder);
    const visible=ref(true), source=ref('first'), preview=ref(false); let acquired=0,released=0,aborted=0,lateResolve;
    const pool={acquire:async(path,signal)=>{
      acquired++; signal.addEventListener('abort',()=>aborted++,{once:true});
      let done=false; const lease={url,blob:path==='bad'?new Blob([new Uint8Array([0])]):blob,
        width:640,height:480,release(){if(!done){done=true;released++;}}};
      return path==='late' ? await new Promise(resolve=>{lateResolve=()=>resolve(lease);}) : lease;
    }};
    const app=createApp({setup:()=>()=>h(MediaImage,{pool,path:source.value,visible:visible.value,preview:preview.value})});
    const wait=async(condition,label)=>{const end=Date.now()+3000;while(!condition()){
      if(Date.now()>end)throw new Error(label);await new Promise(resolve=>setTimeout(resolve,10));}};
    app.mount(holder); let unmounted=false;
    try {
      await wait(()=>holder.querySelector('canvas')?.width===640,'Original image lost its source resolution');
      const image=holder.querySelector('canvas');
      const pixel=image.getContext('2d').getImageData(10,10,1,1).data;
      if(pixel[0]!==24||pixel[1]!==112||pixel[2]!==208||pixel[3]!==255)throw new Error('Original pixels were not drawn');
      for(let cycle=0;cycle<100;cycle++){
        visible.value=false; await nextTick();
        await wait(()=>released===acquired&&image.width===0&&image.height===0,'Hidden image kept its bitmap or lease');
        if(holder.querySelector('canvas')!==image)throw new Error('Closing the image replaced its DOM owner');
        visible.value=true; await nextTick();
        await wait(()=>image.width===640&&image.height===480,'Reopened image did not paint its original lease');
      }
      visible.value=false; await nextTick();
      await wait(()=>released===acquired,'Last visible lease remained held');
      preview.value=true; visible.value=true; await nextTick();
      const physicalWidth=Math.min(640,holder.querySelector('figure').clientWidth*devicePixelRatio);
      await wait(()=>image.width>=physicalWidth&&image.width<=640&&image.width<physicalWidth+64&&
        image.height===Math.round(480*image.width/640), 'Preview exceeded its bounded display resolution or lost its aspect ratio');
      const previewWidth=image.width;
      visible.value=false; await nextTick();
      source.value='bad'; visible.value=true; await nextTick();
      await wait(()=>released===acquired&&holder.querySelector('figcaption')?.textContent.includes('decoded'),
        'Decode failure retained its lease or bitmap');
      if(image.width||image.height)throw new Error('Failed decode retained pixels');
      visible.value=false; await nextTick();
      source.value='late'; visible.value=true; await nextTick();
      await wait(()=>typeof lateResolve==='function','Late image request did not start');
      visible.value=false; await nextTick(); lateResolve();
      await wait(()=>released===acquired,'Late hidden image did not release its returned lease');
      if(image.width||image.height)throw new Error('Late hidden response repopulated the image');
      app.unmount(); unmounted=true;
      if(holder.childElementCount)throw new Error('Unmount retained the image DOM');
      return {cycles:100,acquired,released,aborted,originalResolution:[640,480],previewWidth,
        retainedElementIdentity:true,decodeFailureReleased:true,lateResponseReleased:true};
    } finally {if(!unmounted)app.unmount();holder.remove();URL.revokeObjectURL(url);}
  })()`);
}
async function run() {
  const importMap = fs.readFileSync(path.join(runtime, 'index.html'), 'utf8')
    .match(/<script type="importmap">([^<]+)<\/script>/)?.[1];
  if (!importMap) throw new Error('Missing fixture import map');
  const importMapHash = crypto.createHash('sha256').update(importMap).digest('base64');
  server = http.createServer((request, response) => {
    response.setHeader('Content-Security-Policy', "default-src 'self'; script-src 'self' 'sha256-" + importMapHash + "'; style-src 'self' 'unsafe-inline'; img-src 'self' blob: data:; connect-src 'self'; object-src 'none'");
    const requested = request.url === '/' ? '/index.html' : request.url;
    if (request.method === 'GET' && requested === '/control/v1/status') {
      response.writeHead(200, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
      response.end(JSON.stringify({ fixture: 'native-window-fetch' }));
      return;
    }
    const record = manifest.get(requested) || (!path.extname(requested) ? manifest.get(requested + '.js') : undefined);
    if (request.method !== 'GET' || !record) { response.writeHead(404).end(); return; }
    const raw = fs.readFileSync(path.join(runtime, record.path));
    const type = record.path.endsWith('.html') ? 'text/html; charset=utf-8' : record.path.endsWith('.css') ? 'text/css' : 'text/javascript';
    response.writeHead(200, { 'Content-Type': type, 'Cache-Control': 'no-store' });
    response.end(raw);
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const origin = 'http://127.0.0.1:' + server.address().port;
  window = new BrowserWindow({ width: 1280, height: 900, show: true,
    webPreferences: { sandbox: true, contextIsolation: true, nodeIntegration: false, backgroundThrottling: false } });
  window.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  window.webContents.on('will-navigate', (event, url) => { if (!url.startsWith(origin + '/')) event.preventDefault(); });
  window.webContents.on('console-message', (_event, details) => {
    if (details.level === 'error') consoleErrors.push(details.message);
  });
  await window.loadURL(origin + '/');
  window.focus();
  window.webContents.debugger.attach('1.3');
  const control = await script("import('/tests/frontend/control-contract.js').then(module=>module.runControlContract())");
  if (!Array.isArray(control.checks) || control.checkCount !== control.checks.length || control.checkCount < 5) {
    throw new Error('Control client result identity or checks are incomplete');
  }
  window.show();
  window.focus();
  window.webContents.focus();
  await script(`new Promise((resolve,reject)=>{
    if(document.hasFocus()){resolve(true);return;}
    const timer=setTimeout(()=>{window.removeEventListener('focus',focused);reject(new Error('Native fixture could not acquire document focus'));},2000);
    function focused(){clearTimeout(timer);resolve(true);}
    window.addEventListener('focus',focused,{once:true});
  })`);
  await script('globalThis.nexusUiTests.structural()');
  await script('globalThis.nexusUiTests.prepareAccessibility()');
  const tree = await window.webContents.debugger.sendCommand('Accessibility.getFullAXTree');
  const names = tree.nodes.filter(node => !node.ignored).map(node =>
    node.role?.value + ':' + node.name?.value + (node.description?.value ? ':' + node.description.value : ''));
  await script('globalThis.nexusUiTests.verifyAccessibility(' + JSON.stringify(names) + ')');
  await script('globalThis.nexusUiTests.prepareKeyboardSwitch()');
  await key('Space');
  await script('globalThis.nexusUiTests.verifyKeyboardSwitch()');
  await script('globalThis.nexusUiTests.prepareComposition()');
  await window.webContents.debugger.sendCommand('Input.imeSetComposition', { text: '测试', selectionStart: 2, selectionEnd: 2 });
  await script('globalThis.nexusUiTests.verifyCompositionPending()');
  await window.webContents.debugger.sendCommand('Input.insertText', { text: '测试' });
  await script('globalThis.nexusUiTests.verifyCompositionCommitted()');
  await script('globalThis.nexusUiTests.prepareDialogs()');
  await key('Escape');
  await script('globalThis.nexusUiTests.verifyDialogEscape()');
  const ambientMotion = await checkAmbientMotion();
  const mediaImageOwnership = await checkImageOwnership();
  window.webContents.debugger.detach();
  const result = await script('globalThis.nexusUiTests.finish()');
  result.checks.push('ambient reduced motion resumes, pauses, hides and disposes without late frames');
  result.checks.push('one hundred retained image opens release every hidden and late lease without replacing the DOM owner');
  result.checkCount = result.checks.length;
  if (result.contractFingerprint !== input.contractFingerprint || !Array.isArray(result.checks) || result.checkCount < 25
    || result.checkCount !== result.checks.length) throw new Error('UI result identity or checks are incomplete');
  if (consoleErrors.length) throw new Error('Renderer errors: ' + consoleErrors.join('; '));
  fs.writeFileSync(path.join(output, 'surface.png'), (await window.webContents.capturePage()).toPNG(), { flag: 'wx' });
  save({ ...result, status: 'PASS', browser: process.versions, controlContract: control, ambientMotion, mediaImageOwnership,
    scope: 'New native elements and staged frontend registry in real Electron; contract-only, no product/backend approval' });
  const deadline = Date.now() + 10000;
  while (!fs.existsSync(input.release)) {
    if (Date.now() >= deadline) throw new Error('Owned family readiness deadline exceeded');
    await delay(20);
  }
  const release = JSON.parse(fs.readFileSync(input.release, 'utf8'));
  if (release.format !== 'nexus.test.family-ready' || release.version !== 1 || !release.attributionComplete) {
    throw new Error('Owned family was not fully attributed before exit');
  }
}
app.whenReady().then(run).then(() => {
  window.close();
  server.close(() => app.quit());
}).catch(error => {
  if (completed) fs.writeFileSync(path.join(output, 'shutdown-failure.json'), JSON.stringify({ error: error.message, stages }) + '\n', { flag: 'wx' });
  save({ status: 'FAIL', error: error.message, stack: error.stack, consoleErrors, stages,
    nativeWindowFocused: window?.isFocused(), nativeWebFocused: window?.webContents.isFocused() });
  if (window && !window.isDestroyed()) window.destroy();
  if (server) server.close();
  app.exit(1);
});
setTimeout(() => {
  if (!completed) {
    save({ status: 'FAIL', error: 'Native UI deadline exceeded', stages, consoleErrors,
      nativeWindowFocused: window?.isFocused(), nativeWebFocused: window?.webContents.isFocused(),
      nativeWindowVisible: window?.isVisible(), nativeWindowMinimized: window?.isMinimized(),
      rendererLoading: window?.webContents.isLoading(), rendererCrashed: window?.webContents.isCrashed() });
    app.exit(1);
  }
}, 75000).unref();
