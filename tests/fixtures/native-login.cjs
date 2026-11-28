'use strict';
const { app, BrowserWindow, session, webContents } = require('electron');
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
  if (request.url === '/history') return html(response, "document.body.insertAdjacentHTML('beforeend','<a href=/cookie id=next>Next</a>');");
  return html(response, "localStorage.setItem('unselected','private-fixture-value');");
}
async function listen(handler) {
  const server = http.createServer(handler); await new Promise(resolve => server.listen(0, '127.0.0.1', resolve)); return server;
}
function policy(route) {
  return { id: 'fixture', startUrl: origin + route, origins: [origin], popupOrigins: [],
    cookies: [{ id: 'token', domain: '127.0.0.1', path: '/', name: 'token' }, { id: 'account', domain: '127.0.0.1', path: '/', name: 'account' }],
    storage: [], jsonRequests: [], readySets: [['token', 'account']],
    checks: [{ id: 'token', kind: 'token', path: [], maximumLength: 128 }, { id: 'account', kind: 'digits', path: [], maximumLength: 32 }],
    equalSets: [], pageActions: { stopWhen: [], clicks: [] } };
}
const pause = (ms = 20) => new Promise(resolve => setTimeout(resolve, ms));
async function until(predicate, message) {
  const deadline = performance.now() + 4500;
  do { if (await predicate()) return; await pause(); } while (performance.now() < deadline);
  throw new Error(message);
}
async function run(name, capturePolicy, check, { cancel = false, retry = false, exercise, respond } = {}) {
  const offer = { captureId: crypto.randomUUID(), nonce: crypto.randomUUID(), policy: capturePolicy };
  const privateSession = session.fromPartition('nexus-login-' + offer.captureId + '-' + offer.nonce, { cache: false });
  assert((await privateSession.cookies.get({})).length === 0, 'fixture_partition_not_empty');
  if (respond) privateSession.protocol.handle('https', respond);
  const started = performance.now(); let maximumWindows = 0;
  const observed = setInterval(() => { maximumWindows = Math.max(maximumWindows, BrowserWindow.getAllWindows().length); }, 20);
  let resolve; let reject;
  const finished = new Promise((yes, no) => { resolve = yes; reject = no; });
  let validationAttempts = 0; let completions = 0; let refreshes = 0; let primary;
  const capture = createLoginWindow(offer, undefined, async (status, values) => {
    completions++;
    try {
      assert(BrowserWindow.getAllWindows().length === 0, 'fixture_window_cleanup_missing');
      assert(!primary || primary.isDestroyed(), 'fixture_view_cleanup_missing');
      assert((await privateSession.cookies.get({})).length === 0, 'fixture_cookie_cleanup_missing');
      await check(status, values, performance.now() - started, maximumWindows);
      results.push({ name, status: 'PASS', elapsedMs: performance.now() - started, maximumWindows, windowCleanup: true, cookieCleanup: true, validationAttempts, completions, refreshes }); resolve();
    } catch (error) { reject(error); throw error; }
  }, async () => {
    assert(BrowserWindow.getAllWindows().length > 0, 'fixture_validation_requires_live_window');
    validationAttempts++; return retry !== true || validationAttempts >= 3;
  }, 'light');
  if (cancel === 'early') { await capture.cancel(); await capture.started.catch(() => {}); }
  else
  {
    await capture.started;
    const root = BrowserWindow.getAllWindows()[0];
    primary = webContents.getAllWebContents().find(contents => contents.session === privateSession);
    assert(primary && primary !== root.webContents);
    const read = expression => root.webContents.executeJavaScript(expression);
    const query = action => 'document.querySelector("[data-action=' + action + ']")';
    const enabled = action => read(query(action) + '?.getAttribute("aria-disabled") === "false"');
    const status = key => read('document.getElementById("status")?.textContent === ' + JSON.stringify(text(key)));
    const press = async action => {
      await until(() => enabled(action), 'fixture_action_disabled_' + action);
      await read(query(action) + '.click();true');
    };
    if (retry === 'load') {
      await until(() => status('loginLoadFailed'), 'fixture_load_failure_not_retryable');
      assert(completions === 0 && validationAttempts === 0, 'fixture_load_failure_completed_session');
      await press('reload'); refreshes++;
    }
    if (retry === true) {
      const rejected = async attempts => {
        await until(async () => validationAttempts === attempts && await status('loginRejected'), 'fixture_rejection_missing');
        assert(validationAttempts === attempts && completions === 0 && !root.isDestroyed(), 'fixture_rejection_ended_session');
        assert((await privateSession.cookies.get({ name: 'token' })).length === 1, 'fixture_rejection_lost_cookie');
      };
      await until(() => enabled('complete'), 'fixture_cookie_page_not_ready');
      await pause(300);
      assert.equal(validationAttempts, 0, 'fixture_completed_without_user_action');
      await press('complete');
      await rejected(1);
      await press('reload'); refreshes++;
      await press('complete'); await rejected(2);
    }
    if (exercise) await exercise({ root, primary, read, enabled, press });
    if (cancel === 'window') root.close();
    else if (cancel) await press('cancel');
    else await press('complete');
  }
  await finished; clearInterval(observed);
  if (respond) privateSession.protocol.unhandle('https');
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
  }, { retry: true });
  await run('failed-navigation-refresh', policy('/retry-load'), (status, values) => {
    assert(status === 'completed' && values.token === canary, 'fixture_navigation_retry_failed');
  }, { retry: 'load' });
  const passport = policy('/passport'); passport.cookies = []; passport.checks = [
    { id: 'passport', kind: 'token', path: [], maximumLength: 128 }, { id: 'device', kind: 'header', path: ['id'], maximumLength: 128 }];
  passport.storage = [{ id: 'device', origin, storage: 'localStorage', key: 'device' }];
  passport.jsonRequests = [{ id: 'passport', origin, endpoint: origin + '/passport-api', method: 'POST', selector: ['data', 'content'] }];
  passport.readySets = [['passport', 'device']];
  await run('passive-json-and-storage', passport, (status, values, elapsed) => {
    assert(status === 'completed' && values.passport === 'synthetic-passport', 'fixture_passive_json_failed');
    assert(values.device === '{"id":"synthetic-device"}' && elapsed >= 800, 'fixture_completion_too_early');
    assert(foreignHits === 0, 'fixture_foreign_frame_loaded');
  }, { exercise: async ({ primary }) => {
    await until(() => primary.executeJavaScript('localStorage.getItem("device")?.includes("synthetic-device")'), 'fixture_storage_pending');
  } });
  const popup = policy('/popup'); popup.popupOrigins = [origin];
  await run('owned-popup', popup, (status, values, _elapsed, maximum) => {
    assert(status === 'completed' && values.token === canary && maximum === 2, 'fixture_popup_capture_failed');
    assert(foreignHits === 0, 'fixture_foreign_popup_loaded');
  }, { exercise: async () => {
    await until(() => BrowserWindow.getAllWindows().length === 2, 'fixture_popup_pending');
    await pause(300);
  } });
  await run('cancel-incomplete', policy('/blank'), (status, values) => {
    assert(status === 'canceled' && Object.keys(values).length === 0, 'fixture_cancel_published_values');
  }, { cancel: true });
  await run('cancel-bootstrap', policy('/cookie'), (status, values) => {
    assert(status === 'canceled' && Object.keys(values).length === 0, 'fixture_early_cancel_published_values');
  }, { cancel: 'early' });
  await run('maximize-restore-and-native-close', policy('/cookie'), (status, values) => {
    assert.equal(status, 'canceled');
    assert.equal(Object.keys(values).length, 0);
  }, { cancel: 'window', exercise: async ({ root, primary, enabled }) => {
    await until(() => enabled('complete'), 'fixture_window_not_ready');
    const view = root.contentView.children.find(item => item.webContents === primary);
    const verifyBounds = () => {
      const [width, height] = root.getContentSize();
      const bounds = view.getBounds();
      return bounds.width === width && bounds.height + bounds.y === height;
    };
    const normal = view.getBounds();
    root.maximize();
    primary.emit('unresponsive');
    await until(() => !root.isDestroyed() && root.isMaximized() && verifyBounds(), 'fixture_maximize_lost_view');
    assert.notDeepEqual(view.getBounds(), normal);
    root.unmaximize();
    await until(() => !root.isDestroyed() && !root.isMaximized() && verifyBounds(), 'fixture_restore_lost_view');
    assert(await enabled('complete'));
  } });
  await run('toolbar-navigation-and-sandbox', policy('/history'), status => assert.equal(status, 'canceled'), {
    cancel: true, exercise: async ({ primary, read, enabled, press }) => {
      await until(() => enabled('complete'), 'fixture_history_not_loaded');
      assert.deepEqual(await read('[...document.querySelectorAll("button")].map(button => button.dataset.action)'),
        ['back', 'forward', 'reload', 'cancel', 'complete']);
      assert.equal(await read('document.getElementById("toolbar").getAttribute("focusgroup")'), 'toolbar wrap');
      assert.equal(await primary.executeJavaScript('typeof window.loginShell'), 'undefined');
      assert.equal(await enabled('back'), false, 'fixture_blank_navigation_reachable');
      await primary.executeJavaScript('history.pushState(null, "", "/history#same-document");true');
      await until(() => enabled('back'), 'fixture_same_document_back_unavailable');
      await read('window.loginShell.action("back")');
      await until(() => enabled('forward'), 'fixture_same_document_forward_unavailable');
      await primary.executeJavaScript('document.getElementById("next").click()');
      await until(() => enabled('back'), 'fixture_back_unavailable');
      await press('back');
      await until(() => enabled('forward'), 'fixture_forward_unavailable');
      await press('forward');
      await until(() => enabled('back'), 'fixture_forward_failed');
      assert.equal(new URL(primary.getURL()).pathname, '/cookie');
      await press('reload');
      await until(() => enabled('back'), 'fixture_reload_lost_location');
      assert.equal(new URL(primary.getURL()).pathname, '/cookie');
    }
  });
  const autoOrigin = 'https://entry.nexus.test';
  const entry = policy('/entry');
  entry.startUrl = autoOrigin + '/entry'; entry.origins = [autoOrigin]; entry.cookies = [];
  entry.pageActions = {
    stopWhen: [{ selector: '[role=dialog]', kind: 'visible', labels: [] }, { selector: '.item', kind: 'label', labels: ['登出'] }],
    clicks: [
      { target: { selector: '.item', kind: 'label', labels: ['login', '登入'] }, unique: true, requiresSelector: '#avatar', unlessSelector: '' },
      { target: { selector: '#avatar', kind: 'visible', labels: [] }, unique: false, requiresSelector: '', unlessSelector: '.item' }
    ]
  };
  const contents = `<!doctype html><meta charset="utf-8"><body><script>
    window.clicks = [0, 0];
    setTimeout(() => history.replaceState(null, '', location.href), 50);
    setTimeout(() => {
      document.body.insertAdjacentHTML('beforeend', '<button id="avatar">Avatar</button>');
      document.getElementById('avatar').onclick = () => {
        clicks[0]++;
        document.body.insertAdjacentHTML('beforeend', '<button class="item">登入</button>');
        document.querySelector('.item').onclick = () => {
          clicks[1]++;
          document.body.insertAdjacentHTML('beforeend', '<div role="dialog">Sign in</div>');
        };
      };
    }, 120);
  </script>`;
  await run('bounded-two-step-entry', entry, status => assert.equal(status, 'canceled'), {
    cancel: true, respond: () => new Response(contents, { headers: { 'Content-Type': 'text/html; charset=utf-8' } }),
    exercise: async ({ primary, press }) => {
      const openedOnce = () => primary.executeJavaScript('JSON.stringify(window.clicks) === "[1,1]"');
      await until(openedOnce, 'fixture_delayed_entry_not_clicked');
      await pause(300); assert(await openedOnce());
      const reloaded = new Promise(resolve => primary.once('did-finish-load', resolve));
      await press('reload'); await reloaded;
      await until(openedOnce, 'fixture_reloaded_entry_not_clicked');
      await primary.loadURL(autoOrigin + '/other');
      await pause(500);
      assert.deepEqual(await primary.executeJavaScript('window.clicks'), [0, 0], 'fixture_wrong_path_clicked');
    }
  });
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
