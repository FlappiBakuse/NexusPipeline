'use strict';
const { strictJson } = require('./strict-json.cjs');

function selected(value, path) {
  for (const part of path) {
    if (!value || typeof value !== 'object' || Array.isArray(value) || !Object.hasOwn(value, part)) return undefined;
    value = value[part];
  }
  return typeof value === 'string' && value.length > 0 && value.length <= 32768 ? value : undefined;
}
function normalized(value, check) {
  if (typeof value !== 'string' || value.length < 1 || value.length > 32768) return undefined;
  if (check.path.length) {
    try { value = selected(strictJson(value), check.path); } catch { return undefined; }
  }
  if (typeof value !== 'string' || value.length < 1 || value.length > check.maximumLength) return undefined;
  const patterns = { header: /^[\x21-\x7e]+$/, cookie: /^[\x21\x23-\x2b\x2d-\x3a\x3c-\x5b\x5d-\x7e]+$/,
    token: /^[A-Za-z0-9._~+/-]+={0,2}$/, digits: /^[0-9]+$/ };
  return check.kind === 'nonEmpty' || patterns[check.kind]?.test(value) ? value : undefined;
}
function isReady(policy, values) {
  const valid = Object.create(null); let total = 0;
  const ids = new Set([...policy.cookies, ...policy.storage, ...policy.jsonRequests].map(rule => rule.id));
  for (const [id, value] of Object.entries(values)) {
    if (!ids.has(id)) return false;
    const check = policy.checks.find(check => check.id === id) ?? { kind: 'nonEmpty', path: [], maximumLength: 32768 };
    const checked = normalized(value, check);
    if (checked === undefined || (total += checked.length) > 65536) return false;
    valid[id] = checked;
  }
  for (const set of policy.equalSets) {
    const values = set.filter(id => Object.hasOwn(valid, id)).map(id => valid[id]);
    if (new Set(values).size > 1) return false;
  }
  return policy.readySets.some(set => set.every(id => Object.hasOwn(valid, id)));
}
function origin(url) { try { const value = new URL(url); return !value.username && !value.password ? value.origin : undefined; } catch { return undefined; } }
function endpoint(url) { try { const value = new URL(url); return value.origin + value.pathname; } catch { return undefined; } }

async function collectValues(policy, privateSession, windows, passive) {
  const values = Object.assign(Object.create(null), passive);
  for (const rule of policy.cookies) {
    const matches = (await privateSession.cookies.get({ domain: rule.domain, name: rule.name }))
      .filter(cookie => cookie.domain === rule.domain && cookie.path === rule.path && cookie.name === rule.name);
    if (new Set(matches.map(cookie => cookie.value)).size > 1) throw new Error('browser_login_cookie_conflict');
    if (matches[0]?.value) values[rule.id] = matches[0].value;
  }
  for (const window of windows) {
    if (window.isDestroyed()) continue;
    const frames = window.webContents.mainFrame.framesInSubtree;
    if (frames.length > 64) throw new Error('browser_login_frame_limit');
    for (const frame of frames) {
      const address = origin(frame.url);
      const rules = policy.storage.filter(rule => rule.origin === address);
      if (!rules.length) continue;
      try {
        const captured = await frame.executeJavaScript(`(() => {
          const rules = ${JSON.stringify(rules)};
          const result = Object.create(null);
          for (const rule of rules) {
            const value = window[rule.storage].getItem(rule.key);
            if (typeof value === 'string' && value.length > 0 && value.length <= 32768) result[rule.id] = value;
          }
          return result;
        })()`);
        if (origin(frame.url) !== address || !captured || typeof captured !== 'object' || Array.isArray(captured)) continue;
        for (const rule of rules) if (typeof captured[rule.id] === 'string' && captured[rule.id].length <= 32768) {
          if (values[rule.id] !== undefined && values[rule.id] !== captured[rule.id]) throw new Error('browser_login_storage_conflict');
          values[rule.id] = captured[rule.id];
        }
      } catch (error) {
        if (error?.message === 'browser_login_storage_conflict') throw error;
      }
    }
  }
  return values;
}

function watchResponses(contents, policy, values, pending, stopped, fail) {
  const requests = new Map();
  const debuggerApi = contents.debugger;
  debuggerApi.attach('1.3');
  const message = (_event, method, data) => {
    if (stopped()) return;
    if (method === 'Network.requestWillBeSent') {
      requests.delete(data.requestId);
      const rules = policy.jsonRequests.filter(rule => rule.origin === origin(data.documentURL) &&
        rule.method === data.request.method && rule.endpoint === endpoint(data.request.url));
      if (rules.length && requests.size < 64) requests.set(data.requestId, { rules, accepted: false });
    } else if (method === 'Network.responseReceived') {
      const saved = requests.get(data.requestId);
      if (saved) saved.accepted = data.response.status >= 200 && data.response.status < 300 &&
        saved.rules.every(rule => rule.endpoint === endpoint(data.response.url)) && /json/i.test(data.response.mimeType);
    } else if (method === 'Network.loadingFailed') requests.delete(data.requestId);
    else if (method === 'Network.loadingFinished') {
      const saved = requests.get(data.requestId); requests.delete(data.requestId);
      if (!saved?.accepted || data.encodedDataLength > 32768 || pending.size >= 8) return;
      const work = (async () => {
        try {
          const body = await debuggerApi.sendCommand('Network.getResponseBody', { requestId: data.requestId });
          const bytes = body.base64Encoded ? Buffer.from(body.body, 'base64') : Buffer.from(body.body, 'utf8');
          try {
            if (stopped() || bytes.length > 32768) return;
            const parsed = strictJson(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
            for (const rule of saved.rules) {
              const value = selected(parsed, rule.selector);
              if (value !== undefined) values[rule.id] = value;
            }
          } finally { bytes.fill(0); }
        } catch { }
      })();
      pending.add(work); void work.finally(() => pending.delete(work));
    }
  };
  debuggerApi.on('message', message);
  const ready = (async () => {
    await debuggerApi.sendCommand('Page.enable');
    await debuggerApi.sendCommand('Page.setInterceptFileChooserDialog', { enabled: true });
    await debuggerApi.sendCommand('Network.enable', { maxTotalBufferSize: 262144, maxResourceBufferSize: 65536, maxPostDataSize: 0 });
    await debuggerApi.sendCommand('Network.setCacheDisabled', { cacheDisabled: true });
  })();
  void ready.catch(() => fail());
  return { ready, dispose: () => {
    requests.clear(); debuggerApi.removeListener('message', message);
    if (debuggerApi.isAttached()) debuggerApi.detach();
  } };
}
module.exports = { isReady, collectValues, watchResponses, origin };
