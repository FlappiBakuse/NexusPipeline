'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const crypto = require('node:crypto');
const electron = require('electron');
const { app, BrowserWindow, Menu, shell, dialog } = electron;
const args = process.argv.slice(2);
if (args.length !== 4 || args[0] !== '--output' || !path.isAbsolute(args[1]) || args[2] !== '--mode' ||
    !['desktop', 'browser'].includes(args[3])) throw new Error('explicit_output_and_mode_required');
const output = path.resolve(args[1]);
fs.mkdirSync(output, { recursive: true });
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
let template; let locale = 'zh-CN'; let revision = 1; let currentMode = mode;
let rightClick; let popup;
let modeSaves = 0; let deniedSaves = false; let closePermits = 0;
const warnings = [], opened = [], cases = [];
const values = { autoStart: false, desktop: { mode, minimizeToTray: true, openOnStartup: false }, extensions: [] };
const originalMenu = Menu.buildFromTemplate.bind(Menu);
Menu.buildFromTemplate = entries => { template = entries; return originalMenu(entries); };
const trayOn = electron.Tray.prototype.on;
electron.Tray.prototype.on = function(name, callback) {
  if (name === 'right-click') rightClick = callback;
  return trayOn.call(this, name, callback);
};
electron.Tray.prototype.popUpContextMenu = menu => { popup = menu.items.map(item => item.label); };
shell.openExternal = async value => { opened.push(value); };
dialog.showMessageBox = async options => { warnings.push(options.message); return { response: 0 }; };
function sign(value) {
  const payload = Buffer.from(JSON.stringify(value));
  return { format: 'nexus.desktop.proof', version: 1, algorithm: 'ES256', payloadBase64: payload.toString('base64'),
    signatureBase64: crypto.sign('sha256', payload, { key: key.privateKey, dsaEncoding: 'ieee-p1363' }).toString('base64') };
}
const page = `<!doctype html><html><body><input id="draft"><script>
window.acceptExit=true;
(async()=>{await nexusDesktopChannel.connect();nexusDesktopChannel.prepareExit(async()=>window.acceptExit);window.ready=true;})();
</script></body></html>`;
const server = http.createServer(async (request, response) => {
  try {
    const url = new URL(request.url, 'http://127.0.0.1');
    const chunks = [];
    for await (const chunk of request) chunks.push(chunk);
    const body = chunks.length ? JSON.parse(Buffer.concat(chunks).toString('utf8')) : null;
    let value;
    if (url.pathname === '/') {
      response.writeHead(200, { 'Content-Type': 'text/html', 'Cache-Control': 'no-store' }); response.end(page); return;
    }
    if (url.pathname.endsWith('/desktop/session')) value = sign({ format: 'nexus.desktop.session', version: 1,
      ...identity, correlationId: url.searchParams.get('correlationId'), capabilities: ['close.request', 'file.select', 'folder.select', 'external.open'] });
    else if (url.pathname.endsWith('/desktop/maintenance')) { response.writeHead(204); response.end(); return; }
    else if (url.pathname.endsWith('/desktop/login')) value = sign({ ...body, type: 'offers', offers: [] });
    else if (url.pathname.endsWith('/desktop/mode')) { currentMode = body.mode; value = sign(body); }
    else if (url.pathname.endsWith('/desktop/authorize')) {
      closePermits++;
      value = sign({ ...body, type: 'close.permit', isApproved: true, permitId: crypto.randomUUID(),
        expiresAt: new Date(Date.now() + 30000).toISOString() });
    } else if (url.pathname.endsWith('/settings/interface')) value = { values: { locale } };
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
    scope: 'Actual pinned Electron main, Tray, Menu, BrowserWindow, renderer and IPC; controlled signed HTTP and external-browser/menu-popup I/O ports, not integrated Host',
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
async function click(index) { await template[index].click(); }
async function run() {
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const address = 'http://127.0.0.1:' + server.address().port + '/';
  const profile = path.join(output, 'profile');
  for (const name of ['', 'session', 'dumps']) fs.mkdirSync(path.join(profile, name), { recursive: true });
  process.argv.push('--nexus-bootstrap=' + Buffer.from(JSON.stringify({ address, ...{ instanceId: identity.instanceId }, verificationKey,
    profilePath: profile, visible: false, route: '/', minimizeToTray: true, mode })).toString('base64'));
  require(path.join(source, 'main.cjs'));
  await app.whenReady();
  await waitFor(() => template?.length === 6);
  if (mode === 'browser') {
    assert.equal(BrowserWindow.getAllWindows().length, 0);
    locale = 'en-US'; rightClick();
    await waitFor(() => popup?.[0] === 'Show');
    assert.equal(BrowserWindow.getAllWindows().length, 0);
    cases.push('browser-first-popup-uses-current-saved-locale');
    locale = 'zh-CN'; rightClick(); await waitFor(() => popup?.[0] === '显示');
    await click(0); assert.deepEqual(opened, [address]);
    cases.push('browser-startup-without-window-and-open');
    await click(2); assert.equal(currentMode, 'desktop');
  }
  if (mode === 'desktop') {
    assert.equal(BrowserWindow.getAllWindows().length, 0);
    cases.push('hidden-desktop-startup-creates-no-window');
    await click(0);
  }
  let window = await renderer();
  assert.equal(window.isVisible(), true);
  await click(0); await waitFor(() => window.isVisible());
  await window.webContents.executeJavaScript('document.querySelector("input").value="ordinary-draft"');
  await click(1); assert.equal(window.isVisible(), false);
  await click(0); await waitFor(() => window.isVisible());
  assert.equal(await window.webContents.executeJavaScript('document.querySelector("input").value'), 'ordinary-draft');
  cases.push('actual-hide-show-retains-document-and-draft');
  await window.webContents.executeJavaScript('window.acceptExit=false');
  await click(3);
  assert.equal(await window.webContents.executeJavaScript('document.querySelector("input").value'), 'ordinary-draft');
  const savesBefore = modeSaves;
  await click(2); assert.equal(modeSaves, savesBefore); assert.equal(BrowserWindow.getAllWindows().length, 1);
  await window.webContents.executeJavaScript('window.acceptExit=true');
  cases.push('renderer-refusal-prevents-reload-and-mode-save');
  await click(3); window = await renderer();
  assert.equal(await window.webContents.executeJavaScript('document.querySelector("input").value'), '');
  cases.push('actual-reload-after-renderer-preparation');
  deniedSaves = true; await click(2);
  assert.equal(BrowserWindow.getAllWindows().length, 1); assert.equal(currentMode, 'desktop');
  deniedSaves = false;
  await click(2); await waitFor(() => BrowserWindow.getAllWindows().length === 0);
  assert.equal(currentMode, 'browser'); assert.equal(template[2].checked, true);
  await click(0); assert.equal(opened.at(-1), address);
  await click(2); window = await renderer(); assert.equal(currentMode, 'desktop');
  cases.push('successful-mode-save-releases-and-recreates-real-window');
  locale = 'en-US';
  await window.webContents.executeJavaScript('nexusDesktopChannel.setLocale("en-US")');
  assert.equal(template[0].label, 'Show'); assert.equal(template[2].label, 'Lightweight mode');
  cases.push('existing-renderer-updates-native-menu-language');
  await window.webContents.executeJavaScript('window.acceptExit=false'); await click(5);
  assert.equal(closePermits, 0); assert.equal(window.isDestroyed(), false);
  await window.webContents.executeJavaScript('window.acceptExit=true');
  cases.push('close-refusal-retains-window-without-host-permit');
  const closed = new Promise(resolve => window.once('closed', () => {
    assert.equal(closePermits, 1);
    cases.push('signed-close-permit-destroys-real-window-and-quits');
    receipt('PASS'); server.close(); resolve();
  }));
  const quitting = click(5);
  await closed;
  await quitting;
}
void run().catch(error => {
  receipt('FAIL', error); console.error(error); server.close(); app.exit(1);
});
