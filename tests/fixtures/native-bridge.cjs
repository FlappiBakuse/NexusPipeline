'use strict';
const { app, BrowserWindow } = require('electron');
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const { installNativeBridge } = require('../../apps/desktop/native-bridge.cjs');
const input = process.argv.find(value => value.startsWith('--nexus-test-config='))?.slice(20);
if (!input || !path.isAbsolute(input)) throw new Error('fixture_config_required');
const output = path.dirname(input);
app.setPath('userData', path.join(output, 'user-data'));
app.setPath('sessionData', path.join(output, 'sessions'));
app.setPath('crashDumps', path.join(output, 'dumps'));
app.commandLine.appendSwitch('no-proxy-server');
let stopped = false; let window;
const checks = [];
const timer = setTimeout(() => stop(new Error('fixture_timeout')), 45000);
app.on('window-all-closed', () => {});
const pause = () => new Promise(resolve => setTimeout(resolve, 20));
async function main() {
  while (!fs.existsSync(input)) await pause();
  const config = JSON.parse(fs.readFileSync(input, 'utf8'));
  assert(process.versions.electron === '44.9.0', 'fixture_runtime_version');
  window = new BrowserWindow({ show: false, webPreferences: { preload: path.resolve(__dirname, '../../apps/desktop/preload.cjs'),
    sandbox: true, contextIsolation: true, nodeIntegration: false, webSecurity: true } });
  const page = config.address;
  const bridge = installNativeBridge(window, config, page, () => stop(new Error('fixture_unexpected_quit')));
  await window.loadURL(page); await bridge.start();
  const bound = await window.webContents.executeJavaScript(`(async () => {
    await window.nexusDesktopChannel.connect();
    const response = await fetch('/control/v1/extensions/clients',{method:'POST'});
    if (!response.ok) throw new Error('fixture_grant_failed');
    const grant = await response.json(); await window.nexusDesktopChannel.bindFrontend(grant.clientId,grant.token);
    window.__fixtureGrant = grant; return true;
  })()`);
  assert(bound, 'fixture_native_bind'); checks.push('actual main-frame preload and native TCP binding');
  const route = '/control/v1/extensions/fixture-plugin/frontend/' + config.activationId + '/invoke/';
  const result = await window.webContents.executeJavaScript(`(async () => {
    const response = await fetch(${JSON.stringify(route + 'native-capture')},{method:'POST',headers:{'Content-Type':'application/json',
      'X-Nexus-Frontend-Client':window.__fixtureGrant.token},body:JSON.stringify({operationId:${JSON.stringify(crypto.randomUUID())},input:{}})});
    if (!response.ok) throw new Error('fixture_capture_failed');return response.json();
  })()`);
  assert(result.valid && result.jsonDenied && result.disposed, 'fixture_owned_draft_failed');
  assert(BrowserWindow.getAllWindows().length === 1, 'fixture_login_window_leaked');
  checks.push('actual installed SDK capture and owned draft disposal');
  const operation = crypto.randomUUID();
  await window.webContents.executeJavaScript(`(() => {
    window.__fixtureAbort = new AbortController();
    window.__fixturePending = fetch(${JSON.stringify(route + 'native-capture')},{method:'POST',headers:{'Content-Type':'application/json',
      'X-Nexus-Frontend-Client':window.__fixtureGrant.token},signal:window.__fixtureAbort.signal,
      body:JSON.stringify({operationId:${JSON.stringify(operation)},input:{incomplete:true}})}).catch(()=>undefined); return true;
  })()`);
  while (BrowserWindow.getAllWindows().length < 2) await pause();
  await window.webContents.executeJavaScript('window.__fixtureAbort.abort();true');
  while (!(await (await fetch(new URL('fixture/drained/' + operation, page))).json()).drained) await pause();
  assert(BrowserWindow.getAllWindows().length === 1, 'fixture_canceled_window_leaked');
  checks.push('request cancellation waits for real native cleanup and SDK continuation');
  const retired = await window.webContents.executeJavaScript(`(async () => {
    await window.nexusDesktopChannel.retireFrontend(window.__fixtureGrant.clientId);
    const response=await fetch(${JSON.stringify(route + 'native-state')},{method:'POST',headers:{'Content-Type':'application/json',
      'X-Nexus-Frontend-Client':window.__fixtureGrant.token},body:JSON.stringify({input:{}})});
    const state=await response.json();await fetch('/control/v1/extensions/clients/retire',{method:'POST',headers:{'X-Nexus-Frontend-Client':window.__fixtureGrant.token}});
    window.__fixtureGrant=undefined;return !state.IsBound&&!state.CanCapture;
  })()`);
  assert(retired, 'fixture_retired_native_client'); checks.push('retiring original native grant revokes availability');
  stop();
}
function stop(error) {
  if (stopped) return; stopped = true; clearTimeout(timer);
  for (const item of BrowserWindow.getAllWindows()) item.destroy();
  fs.writeFileSync(path.join(output, 'receipt.json'), JSON.stringify({ status: error ? 'FAIL' : 'PASS', checks,
    error: error ? String(error.message) : undefined, runtime: process.versions.electron, scope: 'Actual Host HTTP sources, installed SDK, production Electron IPC and native login bridge; synthetic loopback only' }, null, 2) + '\n');
  app.exit(error ? 1 : 0);
}
void app.whenReady().then(main).catch(stop);
