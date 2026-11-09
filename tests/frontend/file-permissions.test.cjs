'use strict';
const assert = require('node:assert/strict');
const test = require('node:test');
const { installFilePermissions } = require('../../apps/desktop/file-permissions.cjs');
const { setLocale } = require('../../apps/desktop/native-text.cjs');

function fixture() {
  let check, request;
  const events = new Map();
  const state = { prompts: 0, reply: 0, duringPrompt: undefined };
  const page = 'http://127.0.0.1:1234/';
  const contents = { isDestroyed: () => false, mainFrame: { url: page + '#/history' },
    on: (name, listener) => { events.set(name, listener); },
    session: { setPermissionCheckHandler: value => { check = value; },
      setPermissionRequestHandler: value => { request = value; } } };
  const window = { isDestroyed: () => false, webContents: contents };
  const selected = { requestingUrl: contents.mainFrame.url, isDirectory: false,
    fileAccessType: 'writable', filePath: 'D:\\Projects\\Temp\\log.txt' };
  installFilePermissions(window, page, { showMessageBoxSync: (owner, options) => {
    state.options = options;
    assert.equal(owner, window); assert.equal(options.detail, selected.filePath);
    state.prompts++; state.duringPrompt?.(); return state.reply;
  } });
  return { state, selected, page, contents,
    check: (details = selected, source = null, origin = page, permission = 'fileSystem') => check(source, permission, origin, details),
    request: (details = selected, source = contents, permission = 'fileSystem') => {
      let answer; request(source, permission, value => { answer = value; }, details); return answer;
    },
    navigate: () => events.get('did-start-navigation')({}, page, false, true) };
}

test('save-picker status checks with no WebContents require native consent and retain bounded document grants', () => {
  const { state, selected, check, request, navigate } = fixture();
  const nativeStatus = { ...selected }; delete nativeStatus.requestingUrl;
  assert.equal(check(nativeStatus), false);
  assert.equal(check(nativeStatus), false);
  assert.equal(state.prompts, 1);
  navigate(); state.reply = 1;
  assert.equal(check(nativeStatus), true);
  assert.equal(check(nativeStatus), true);
  assert.equal(check({ ...nativeStatus, fileAccessType: 'readable' }), true);
  assert.equal(request(), true);
  assert.equal(state.prompts, 2);
  navigate(); state.reply = 0;
  assert.equal(check(nativeStatus), false);
  assert.equal(state.prompts, 3);
  navigate(); setLocale('en-US');
  try {
    assert.equal(check(nativeStatus), false);
    assert.equal(state.options.title, 'Save file');
    assert.deepEqual(state.options.buttons, ['Cancel', 'Allow save']);
  } finally { setLocale('zh-CN'); }
});

test('unrelated permissions, origins, documents, directory grants and malformed file checks are denied without prompts', () => {
  const { selected, check, contents, state, request } = fixture();
  for (const details of [{ ...selected, isDirectory: true }, { ...selected, filePath: 'relative.txt' },
    { ...selected, fileAccessType: 'unknown' }, { ...selected, requestingUrl: 'https://other.test/' },
    { ...selected, fileAccessType: 'readable' }, null]) {
    assert.equal(check(details), false);
  }
  assert.equal(check(selected, { ...contents }), false);
  assert.equal(check(selected, null, 'https://other.test/'), false);
  assert.equal(check(selected, null, 'http://127.0.0.1:1234/', 'geolocation'), false);
  assert.equal(request(selected, null), false);
  assert.equal(request({ ...selected, requestingUrl: undefined }), false);
  assert.equal(state.prompts, 0);
});

test('native prompt cannot grant after navigation and does not admit reentrant permission checks', () => {
  const f = fixture();
  f.state.reply = 1;
  f.state.duringPrompt = () => {
    assert.equal(f.check(), false);
    f.contents.mainFrame.url = 'https://other.test/';
    f.navigate();
  };
  assert.equal(f.check(), false);
  assert.equal(f.check(), false);
  assert.equal(f.state.prompts, 1);
});
