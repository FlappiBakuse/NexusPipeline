'use strict';
const { app, BrowserWindow, session, Menu } = require('electron');
const assert = require('node:assert/strict');
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { createLoginWindow } = require('../../apps/desktop/login-window.cjs');
const { text } = require('../../apps/desktop/native-text.cjs');

const output = process.argv.find(value => value.startsWith('--nexus-test-root='))?.slice(18);
if (!output || !path.isAbsolute(output)) throw new Error('fixture_output_required');
fs.mkdirSync(output, { recursive: true });
app.setPath('userData', path.join(output, 'user-data'));
app.setPath('sessionData', path.join(output, 'sessions'));
app.setPath('crashDumps', path.join(output, 'dumps'));
app.commandLine.appendSwitch('no-proxy-server');
const results = []; let server; let foreign; let origin; let foreignOrigin; let foreignHits = 0;
const diagnostics = [];
let stopping = false; let navigationAttempts = 0;
const canary = 'synthetic_fixture_login_token';
const watchdog = setTimeout(() => stop(new Error('fixture_timeout')), 35000);
app.on('window-all-closed', () => {});
app.on('web-contents-created', (_event, contents) => {
  contents.on('render-process-gone', (_event, details) => diagnostics.push({ event: 'render-process-gone', reason: details.reason, exitCode: details.exitCode }));
  const send = contents.debugger.sendCommand.bind(contents.debugger);
  contents.debugger.sendCommand = async (method, ...args) => {
    try { const result = await send(method, ...args); diagnostics.push({ method, status: 'PASS' }); return result; }
    catch (error) { diagnostics.push({ method, status: 'FAIL', error: String(error.message) }); throw error; }
  };
});

function html(response, script, headers = {}) {
  response.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', ...headers });
  response.end('<!doctype html><meta charset="utf-8"><title>Isolated login fixture</title><body><script>' + script + '</script></body>');
}
function serve(request, response) {
  if (request.url === '/retry-load' && navigationAttempts++ === 0) { request.socket.destroy(); return; }
  if (request.url === '/retry-load') request.url = '/cookie';
  if (request.url === '/cookie') return html(response, '', { 'Set-Cookie': ['token=' + canary + '; Path=/; HttpOnly; SameSite=Lax', 'account=123; Path=/; HttpOnly; SameSite=Lax'] });
  if (request.url === '/passport') return html(response,
    "localStorage.setItem('device','{\"id\":123}');fetch('/passport-api',{method:'POST'});" +
    "setTimeout(()=>localStorage.setItem('device','{\"id\":\"synthetic-device\"}'),900);" +
    "const frame=document.createElement('iframe');frame.src='" + foreignOrigin + "/foreign';document.body.append(frame);");
  if (request.url === '/passport-api' && request.method === 'POST') {
    response.writeHead(200, { 'Content-Type': 'application/json' }); return response.end(JSON.stringify({ data: { content: 'synthetic-passport' } }));
  }
  if (request.url === '/popup') return html(response, "setTimeout(()=>{window.open('/popup-ready');window.open('" + foreignOrigin + "/forbidden');},100);");
  if (request.url === '/popup-ready') return html(response, '', { 'Set-Cookie': ['token=' + canary + '; Path=/; HttpOnly; SameSite=Lax', 'account=123; Path=/; SameSite=Lax'] });
  return html(response, "localStorage.setItem('unselected','private-fixture-value');");
}
async function listen(handler) {
  const server = http.createServer(handler); await new Promise(resolve => server.listen(0, '127.0.0.1', resolve)); return server;
}
function policy(route) {
  return { id: 'fixture', startUrl: origin + route, origins: [origin], popupOrigins: [],
    cookies: [{ id: 'token', domain: '127.0.0.1', path: '/', name: 'token' }, { id: 'account', domain: '127.0.0.1', path: '/', name: 'account' }],
    storage: [], jsonRequests: [], readySets: [['token', 'account']],
    checks: [{ id: 'token', kind: 'token', path: [], maximumLength: 128 }, { id: 'account', kind: 'digits', path: [], maximumLength: 32 }], equalSets: [] };
}
async function run(name, capturePolicy, check, cancel = false, retry = false) {
  const offer = { captureId: crypto.randomUUID(), nonce: crypto.randomUUID(), policy: capturePolicy };
  const privateSession = session.fromPartition('nexus-login-' + offer.captureId + '-' + offer.nonce, { cache: false });
  assert((await privateSession.cookies.get({})).length === 0, 'fixture_partition_not_empty');
  const started = performance.now(); let maximumWindows = 0;
  const observed = setInterval(() => { maximumWindows = Math.max(maximumWindows, BrowserWindow.getAllWindows().length); }, 20);
  let resolve; let reject;
  const finished = new Promise((yes, no) => { resolve = yes; reject = no; });
  let nativeMenu; let validationAttempts = 0; let completions = 0; let refreshes = 0;
  const buildMenu = Menu.buildFromTemplate;
  if (retry) Menu.buildFromTemplate = (...arguments_) => { nativeMenu = buildMenu.apply(Menu, arguments_); return nativeMenu; };
  const capture = createLoginWindow(offer, undefined, async (status, values) => {
    completions++;
    try {
      assert(BrowserWindow.getAllWindows().length === 0, 'fixture_window_cleanup_missing');
      assert((await privateSession.cookies.get({})).length === 0, 'fixture_cookie_cleanup_missing');
      await check(status, values, performance.now() - started, maximumWindows);
      results.push({ name, status: 'PASS', elapsedMs: performance.now() - started, maximumWindows, windowCleanup: true, cookieCleanup: true, validationAttempts, completions, refreshes }); resolve();
    } catch (error) { reject(error); throw error; }
  }, async () => {
    assert(BrowserWindow.getAllWindows().length > 0, 'fixture_validation_requires_live_window');
    validationAttempts++; return retry !== true || validationAttempts >= 3;
  });
  Menu.buildFromTemplate = buildMenu;
  if (cancel === 'early') { await capture.cancel(); await capture.started.catch(() => {}); }
  else
  {
    await capture.started;
    if (retry === 'load') {
      const root = BrowserWindow.getAllWindows()[0];
      assert(!root.isDestroyed() && root.getTitle().includes(text('loginLoadFailed')), 'fixture_load_failure_not_retryable');
      assert(completions === 0 && validationAttempts === 0, 'fixture_load_failure_completed_session');
      nativeMenu.items.flatMap(item => item.submenu?.items ?? []).find(item => item.label === text('loginRefresh')).click();
      refreshes++;
    }
    if (retry === true) {
      const root = BrowserWindow.getAllWindows()[0];
      const rejected = async attempts => {
        const deadline = performance.now() + 3000;
        while ((validationAttempts !== attempts || !root.getTitle().includes(text('loginRejected'))) && performance.now() < deadline)
          await new Promise(resolve => setTimeout(resolve, 20));
        await new Promise(resolve => setTimeout(resolve, 300));
        assert(validationAttempts === attempts && completions === 0 && !root.isDestroyed(), 'fixture_rejection_ended_session');
        assert((await privateSession.cookies.get({ name: 'token' })).length === 1, 'fixture_rejection_lost_cookie');
      };
      const action = label => nativeMenu.items.flatMap(item => item.submenu?.items ?? []).find(item => item.label === text(label));
      await rejected(1);
      const loaded = new Promise(resolve => root.webContents.once('did-finish-load', resolve));
      action('loginRefresh').click(); await loaded; refreshes++;
      await rejected(2); action('loginComplete').click();
    }
    if (cancel) { await new Promise(resolve => setTimeout(resolve, 100)); await capture.cancel(); }
  }
  await finished; clearInterval(observed);
  assert(completions === 1 && (retry !== true || validationAttempts === 3 && refreshes === 1), 'fixture_completion_count_invalid');
}
async function main() {
  assert(process.versions.electron === '44.9.0', 'fixture_runtime_version_mismatch');
  foreign = await listen((_request, response) => { foreignHits++; html(response, "localStorage.setItem('device','foreign');"); });
  foreignOrigin = 'http://127.0.0.1:' + foreign.address().port;
  server = await listen(serve); origin = 'http://127.0.0.1:' + server.address().port;
  await run('httpOnly-cookie', policy('/cookie'), (status, values) => {
    assert(status === 'completed' && values.token === canary && values.account === '123', 'fixture_cookie_capture_failed');
    assert(Object.keys(values).length === 2 && !Object.hasOwn(values, 'unselected'), 'fixture_selector_leak');
  }, false, true);
  await run('failed-navigation-refresh', policy('/retry-load'), (status, values) => {
    assert(status === 'completed' && values.token === canary, 'fixture_navigation_retry_failed');
  }, false, 'load');
  const passport = policy('/passport'); passport.cookies = []; passport.checks = [
    { id: 'passport', kind: 'token', path: [], maximumLength: 128 }, { id: 'device', kind: 'header', path: ['id'], maximumLength: 128 }];
  passport.storage = [{ id: 'device', origin, storage: 'localStorage', key: 'device' }];
  passport.jsonRequests = [{ id: 'passport', origin, endpoint: origin + '/passport-api', method: 'POST', selector: ['data', 'content'] }];
  passport.readySets = [['passport', 'device']];
  await run('passive-json-and-storage', passport, (status, values, elapsed) => {
    assert(status === 'completed' && values.passport === 'synthetic-passport', 'fixture_passive_json_failed');
    assert(values.device === '{"id":"synthetic-device"}' && elapsed >= 800, 'fixture_completion_too_early');
    assert(foreignHits === 0, 'fixture_foreign_frame_loaded');
  });
  const popup = policy('/popup'); popup.popupOrigins = [origin];
  await run('owned-popup', popup, (status, values, _elapsed, maximum) => {
    assert(status === 'completed' && values.token === canary && maximum === 2, 'fixture_popup_capture_failed');
    assert(foreignHits === 0, 'fixture_foreign_popup_loaded');
  });
  await run('cancel-incomplete', policy('/blank'), (status, values) => {
    assert(status === 'canceled' && Object.keys(values).length === 0, 'fixture_cancel_published_values');
  }, true);
  await run('cancel-bootstrap', policy('/cookie'), (status, values) => {
    assert(status === 'canceled' && Object.keys(values).length === 0, 'fixture_early_cancel_published_values');
  }, 'early');
  stop();
}
function stop(error) {
  if (stopping) return; stopping = true;
  clearTimeout(watchdog); for (const window of BrowserWindow.getAllWindows()) window.destroy();
  server?.close(); foreign?.close();
  const receipt = { status: error ? 'FAIL' : 'PASS', runtime: process.versions.electron, scope: 'Actual Electron isolated windows, loopback synthetic login fixtures only',
    cases: results, diagnostics, error: error ? String(error.message) : undefined, realAccountRequests: 0 };
  fs.writeFileSync(path.join(output, 'receipt.json'), JSON.stringify(receipt, null, 2) + '\n'); app.exit(error ? 1 : 0);
}
void app.whenReady().then(main).catch(stop);
