'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const net = require('node:net');
const crypto = require('node:crypto');
const electron = require('electron');
const { app, BrowserWindow, Menu, shell, dialog } = electron;
const args = process.argv.slice(2);
if (args.length !== 4 || args[0] !== '--output' || !path.isAbsolute(args[1]) || args[2] !== '--mode' ||
    !['desktop', 'browser', 'failed'].includes(args[3])) throw new Error('explicit_output_and_mode_required');
const output = path.resolve(args[1]);
fs.mkdirSync(output, { recursive: true });
process.prependListener('uncaughtException', error => fs.writeFileSync(path.join(output, 'uncaught.log'), String(error.stack)));
const runId = new Date().toISOString().replaceAll(/[-:.]/g, '');
const mode = args[3];
const started = performance.now();
const source = path.resolve(__dirname, '../../apps/desktop');
const files = fs.readdirSync(source).filter(name => name.endsWith('.cjs')).sort().map(name => ({
  path: name, sha256: crypto.createHash('sha256').update(fs.readFileSync(path.join(source, name))).digest('hex')
}));
const identity = { instanceId: crypto.randomUUID(), clientId: crypto.randomUUID(), sessionId: crypto.randomUUID() };
const key = crypto.generateKeyPairSync('ec', { namedCurve: 'prime256v1' });
const verificationKey = key.publicKey.export({ type: 'spki', format: 'der' }).toString('base64');
let locale = 'zh-CN'; let revision = 1; let currentMode = mode;
let rightClick, leftClick, nextAction = 0, peer, lastMenu;
const presentations = []; let dismissed = false;
let modeSaves = 0; let deniedSaves = false; let closePermits = 0;
const warnings = [], opened = [], cases = [];
const values = { autoStart: false, desktop: { mode, minimizeToTray: true, openOnStartup: false }, extensions: [] };
const pipeName = 'NexusDesktop-' + crypto.randomUUID().replaceAll('-', '');
const nonce = crypto.randomUUID().replaceAll('-', '');
const pipes = net.createServer(socket => {
  peer = socket; let pending = '';
  socket.on('data', data => {
    pending += data.toString('utf8'); let index;
    while ((index = pending.indexOf('\n')) >= 0) {
      const message = JSON.parse(pending.slice(0, index)); pending = pending.slice(index + 1);
      if (message.kind === 'hello') assert.equal(message.nonce, nonce);
      else if (message.kind === 'dismiss') dismissed = true;
      else if (message.kind === 'presented') presentations.push(message.surface);
      else if (message.kind === 'menu') {
        lastMenu = message; socket.write(JSON.stringify({ kind: 'menu', id: message.id, action: nextAction }) + '\n');
      }
    }
  });
});
const trayOn = electron.Tray.prototype.on;
electron.Tray.prototype.on = function(name, callback) {
  if (name === 'right-click') rightClick = callback;
  if (name === 'click') leftClick = callback;
  return trayOn.call(this, name, callback);
};
shell.openExternal = async value => { opened.push(value); };
dialog.showMessageBox = async options => { warnings.push(options.message); return { response: 0 }; };
function sign(value) {
  const payload = Buffer.from(JSON.stringify(value));
  return { format: 'nexus.desktop.proof', version: 1, algorithm: 'ES256', payloadBase64: payload.toString('base64'),
    signatureBase64: crypto.sign('sha256', payload, { key: key.privateKey, dsaEncoding: 'ieee-p1363' }).toString('base64') };
}
const page = `<!doctype html><html><head><meta charset="utf-8"></head><body><input id="draft"><script>
window.acceptExit=true;
(async()=>{await nexusDesktopChannel.connect();nexusDesktopChannel.prepareExit(async()=>window.acceptExit);
await nexusDesktopChannel.preparation({stage:'准备页面数据',done:2,total:5});window.ready=true;
if(HOLD_PREPARATION)await new Promise(resolve=>window.releaseStartup=resolve);await nexusDesktopChannel.presented();})();
</script></body></html>`;
let holdPage = mode === 'desktop';
let sessionRequests = 0;
let releaseLocale;
const localeReady = mode === 'desktop' ? new Promise(resolve => { releaseLocale = resolve; }) : Promise.resolve();
const server = http.createServer(async (request, response) => {
  try {
    const url = new URL(request.url, 'http://127.0.0.1');
    const chunks = [];
    for await (const chunk of request) chunks.push(chunk);
    const body = chunks.length ? JSON.parse(Buffer.concat(chunks).toString('utf8')) : null;
    let value;
    if (url.pathname === '/') {
      response.writeHead(200, { 'Content-Type': 'text/html', 'Cache-Control': 'no-store' });
      response.end(page.replace('HOLD_PREPARATION', String(holdPage))); holdPage = false; return;
    }
    if (url.pathname.endsWith('/desktop/session')) {
      sessionRequests++;
      value = sign({ format: 'nexus.desktop.session', version: 1,
        ...identity, correlationId: url.searchParams.get('correlationId'), capabilities: ['close.request', 'file.select', 'folder.select', 'external.open'] });
    }
    else if (url.pathname.endsWith('/desktop/maintenance')) { response.writeHead(204); response.end(); return; }
    else if (url.pathname.endsWith('/desktop/login')) value = sign({ ...body, type: 'offers', offers: [] });
    else if (url.pathname.endsWith('/desktop/mode')) { currentMode = body.mode; value = sign(body); }
    else if (url.pathname.endsWith('/desktop/authorize')) {
      closePermits++;
      value = sign({ ...body, type: 'close.permit', isApproved: true, permitId: crypto.randomUUID(),
        expiresAt: new Date(Date.now() + 30000).toISOString() });
    } else if (url.pathname.endsWith('/settings/interface')) {
      await localeReady; value = { values: { locale } };
    }
    else if (url.pathname.endsWith('/settings')) {
      if (request.method === 'POST') {
        if (deniedSaves) { response.writeHead(409); response.end('{}'); return; }
        assert.equal(body.expectedRevision, revision);
        values.desktop = body.values.desktop; revision++; modeSaves++;
      }
      value = { saved: { revision, values } };
    } else { response.writeHead(404); response.end(); return; }
    response.writeHead(200, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' }); response.end(JSON.stringify(value));
  } catch { response.writeHead(500); response.end(); }
});
function receipt(status, error) {
  const unchanged = files.every(file => crypto.createHash('sha256').update(fs.readFileSync(path.join(source, file.path))).digest('hex') === file.sha256);
  if (!unchanged) status = 'FAIL';
  fs.writeFileSync(path.join(output, 'receipt-' + runId + '.json'), JSON.stringify({ status,
    scope: 'Actual pinned Electron main, Tray, BrowserWindow, startup view and IPC; controlled signed HTTP, startup pipe and native-menu selection, not integrated Host',
    initialMode: mode, command: process.argv, electronVersion: process.versions.electron, processId: process.pid,
    runtimeSha256: crypto.createHash('sha256').update(fs.readFileSync(process.execPath)).digest('hex'),
    files, sourceUnchanged: unchanged, fixtureSha256: crypto.createHash('sha256').update(fs.readFileSync(__filename)).digest('hex'),
    cases, modeSaves, currentMode, closePermits, opened, warnings, elapsedMilliseconds: performance.now() - started,
    processMetrics: app.isReady() ? app.getAppMetrics() : [], error: error?.stack }, null, 2) + '\n');
}
async function waitFor(condition) {
  const until = performance.now() + 15000;
  while (!await condition()) {
    if (performance.now() >= until) throw new Error('desktop_fixture_timeout');
    await new Promise(resolve => setTimeout(resolve, 25));
  }
}
async function renderer() {
  await waitFor(async () => BrowserWindow.getAllWindows().length === 1 &&
    await BrowserWindow.getAllWindows()[0].webContents.executeJavaScript('window.ready === true'));
  return BrowserWindow.getAllWindows()[0];
}
async function click(action) { nextAction = action; await rightClick(); }
async function run() {
  await new Promise(resolve => pipes.listen('\\\\.\\pipe\\' + pipeName, resolve));
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const address = 'http://127.0.0.1:' + server.address().port + '/';
  const profile = path.join(output, 'profile');
  for (const name of ['', 'session', 'dumps']) fs.mkdirSync(path.join(profile, name), { recursive: true });
  process.argv.push('--nexus-bootstrap=' + Buffer.from(JSON.stringify({ startupPipe: pipeName, nonce, locale,
    instanceId: identity.instanceId, verificationKey, profilePath: profile, visible: mode !== 'browser',
    route: '/', minimizeToTray: true, mode: mode === 'failed' ? 'desktop' : mode })).toString('base64'));
  require(path.join(source, 'main.cjs'));
  await app.whenReady(); await waitFor(() => peer && rightClick);
  let progressWindow;
  if (mode !== 'browser') {
    await waitFor(() => BrowserWindow.getAllWindows().length === 1 && BrowserWindow.getAllWindows()[0].isVisible());
    progressWindow = BrowserWindow.getAllWindows()[0];
    assert.equal(progressWindow.contentView.children.length, 1);
    progressWindow.close(); assert.equal(progressWindow.isVisible(), false);
    cases.push('early-progress-window-close-hides-without-cancelling');
  } else {
    assert.equal(BrowserWindow.getAllWindows().length, 0);
    cases.push('browser-startup-has-no-renderer');
  }
  await click(0); assert.equal(lastMenu.busy, true);
  cases.push('startup-menu-disables-unavailable-actions');
  if (mode === 'failed') {
    peer.write(JSON.stringify({ kind: 'failed', reason: 'fixture_admission_rejected', errorCode: 7 }) + '\n');
    const view = progressWindow.contentView.children[0].webContents;
    await waitFor(() => view.executeJavaScript('document.querySelector("#actions").hidden === false'));
    assert.equal(await view.executeJavaScript('document.querySelector("#diagnostic").textContent'), 'fixture_admission_rejected\n7');
    peer.write(JSON.stringify({ kind: 'stage', stage: 1, total: 12, elapsedMilliseconds: 10 }) + '\n');
    await leftClick();
    assert.equal(progressWindow.isVisible(), true);
    await click(0); assert.equal(lastMenu.busy, false);
    await view.executeJavaScript('document.querySelector("#exit").click()');
    await waitFor(() => dismissed);
    cases.push('failed-startup-remains-readable-and-exits-only-on-request');
    receipt('PASS'); server.close(); pipes.close(); app.exit(0); return;
  }
  peer.write(JSON.stringify({ kind: 'stage', stage: 11, total: 12, elapsedMilliseconds: 9000 }) + '\n');
  peer.write(JSON.stringify({ kind: 'ready', config: { address, mode, route: '/', minimizeToTray: true } }) + '\n');
  peer.write(JSON.stringify({ kind: 'complete' }) + '\n');
  if (mode === 'desktop') {
    const preparingWindow = await renderer();
    assert.equal(sessionRequests, 1);
    cases.push('initial-document-reuses-its-verified-session-without-pre-navigation-session');
    const view = preparingWindow.contentView.children[0].webContents;
    await waitFor(() => view.executeJavaScript('document.querySelector("#stage").textContent.includes("2/5")'));
    assert.equal(presentations.includes('console'), false);
    assert.equal(preparingWindow.isVisible(), false);
    const elapsed = () => view.executeJavaScript('Number(document.querySelector("#elapsed").textContent.match(/\\d+/)?.[0] || 0)');
    await waitFor(async () => await elapsed() >= 10);
    const before = await elapsed();
    for (let done = 3; done <= 5; done++) {
      await preparingWindow.webContents.executeJavaScript(`nexusDesktopChannel.preparation({stage:'准备页面数据',done:${done},total:5})`);
      await new Promise(resolve => setTimeout(resolve, 300));
      assert.ok(await elapsed() >= before);
    }
    await waitFor(async () => await elapsed() > before);
    cases.push('startup-elapsed-keeps-advancing-through-repeated-preparation-updates');
    await preparingWindow.webContents.executeJavaScript('window.releaseStartup()');
    cases.push('frontend-preparation-updates-overlay-and-waits-for-first-usable-render');
  }
  await waitFor(() => presentations.includes(mode === 'desktop' ? 'console' : 'tray'));
  if (mode === 'desktop') {
    assert.equal(progressWindow.contentView.children.length, 0);
    releaseLocale(); cases.push('console-presentation-does-not-wait-for-tray-locale');
  }
  await click(0); assert.equal(lastMenu.busy, false);
  if (mode === 'desktop') {
    assert.equal(BrowserWindow.getAllWindows()[0], progressWindow);
    assert.equal(progressWindow.isVisible(), false);
    assert.equal(progressWindow.contentView.children.length, 0);
    cases.push('same-window-ready-keeps-user-hidden-intent');
    await leftClick();
  } else {
    await leftClick(); assert.deepEqual(opened, [address]);
    await click(1); assert.equal(currentMode, 'desktop');
  }
  let window = await renderer();
  assert.equal(window.isVisible(), true);
  await leftClick(); await waitFor(() => window.isVisible());
  await window.webContents.executeJavaScript('document.querySelector("input").value="ordinary-draft"');
  window.close(); assert.equal(window.isVisible(), false);
  await leftClick(); await waitFor(() => window.isVisible());
  assert.equal(await window.webContents.executeJavaScript('document.querySelector("input").value'), 'ordinary-draft');
  cases.push('actual-hide-show-retains-document-and-draft');
  await window.webContents.executeJavaScript('window.acceptExit=false');
  await click(2);
  assert.equal(await window.webContents.executeJavaScript('document.querySelector("input").value'), 'ordinary-draft');
  const savesBefore = modeSaves;
  await click(1); assert.equal(modeSaves, savesBefore); assert.equal(BrowserWindow.getAllWindows().length, 1);
  await window.webContents.executeJavaScript('window.acceptExit=true');
  cases.push('renderer-refusal-prevents-reload-and-mode-save');
  await click(2); window = await renderer();
  assert.equal(await window.webContents.executeJavaScript('document.querySelector("input").value'), '');
  cases.push('actual-reload-after-renderer-preparation');
  deniedSaves = true; await click(1);
  assert.equal(BrowserWindow.getAllWindows().length, 1); assert.equal(currentMode, 'desktop');
  deniedSaves = false;
  await click(1); await waitFor(() => BrowserWindow.getAllWindows().length === 0);
  assert.equal(currentMode, 'browser'); await click(0); assert.equal(lastMenu.browser, true);
  await leftClick(); assert.equal(opened.at(-1), address);
  await click(1); window = await renderer(); assert.equal(currentMode, 'desktop');
  cases.push('successful-mode-save-releases-and-recreates-real-window');
  locale = 'en-US';
  await window.webContents.executeJavaScript('nexusDesktopChannel.setLocale("en-US")');
  await click(0); assert.equal(lastMenu.locale, 'en-US');
  cases.push('existing-renderer-updates-native-menu-language');
  await window.webContents.executeJavaScript('window.acceptExit=false'); await click(3);
  assert.equal(closePermits, 0); assert.equal(window.isDestroyed(), false);
  await window.webContents.executeJavaScript('window.acceptExit=true');
  cases.push('close-refusal-retains-window-without-host-permit');
  const closed = new Promise(resolve => window.once('closed', () => {
    assert.equal(closePermits, 1);
    cases.push('signed-close-permit-destroys-real-window-and-quits');
    receipt('PASS'); server.close(); pipes.close(); resolve();
  }));
  const quitting = click(3);
  await closed;
  await quitting;
}
void run().catch(error => {
  receipt('FAIL', error); console.error(error); server.close(); app.exit(1);
});
