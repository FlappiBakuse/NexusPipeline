'use strict';
const crypto = require('node:crypto');
const { readLogin, responseText } = require('./proof-reader.cjs');
const { createLoginWindow } = require('./login-window.cjs');

function installLoginBridge(config, parent, connect, generation) {
  const active = new Map();
  const stopping = new AbortController();
  let ended = false; let timer; let polling;
  const identity = session => ({ format: 'nexus.desktop.login', version: 1, instanceId: session.instanceId,
    nativeClientId: session.clientId, sessionId: session.sessionId, correlationId: crypto.randomUUID() });
  async function post(request) {
    const response = await fetch(new URL('control/v1/desktop/login', config.address), { method: 'POST',
      headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(request), redirect: 'error',
      signal: request.type === 'poll' ? AbortSignal.any([stopping.signal, AbortSignal.timeout(30000)]) :
        AbortSignal.timeout(request.type === 'candidate' ? 60000 : 5000) });
    return readLogin(await responseText(response), config.verificationKey, request);
  }
  async function complete(state, status, values) {
    try {
      await post({ ...identity(state.session), type: 'complete', captureId: state.offer.captureId, nonce: state.offer.nonce, status, values });
      active.delete(state.offer.captureId);
    } catch {
      state.cleaned = true; state.window = undefined;
    }
  }
  async function processOffer(offer, session, stale) {
    const prior = active.get(offer.captureId);
    if (prior && (prior.offer.nonce !== offer.nonce || prior.session.sessionId !== session.sessionId)) throw new Error('desktop_login_identity_changed');
    if (prior) {
      if (prior.cleaned) await complete(prior, 'failed', Object.create(null));
      else if (offer.isCanceled || stale) await prior.window.cancel();
      else await prior.window.retry();
      return;
    }
    if (active.size >= 128) throw new Error('desktop_login_limit');
    const state = { offer, session, cleaned: false, window: undefined }; active.set(offer.captureId, state);
    if (offer.isCanceled || stale) { await complete(state, 'canceled', Object.create(null)); return; }
    state.window = createLoginWindow(offer, typeof parent === 'function' ? parent() : parent, (status, values) => complete(state, status, values), async values => {
      const result = await post({ ...identity(session), type: 'candidate', captureId: offer.captureId, nonce: offer.nonce, values });
      return result.accepted;
    });
  }
  async function poll() {
    if (ended) return;
    try {
      const current = generation(); const session = await connect();
      const response = await post({ ...identity(session), type: 'poll' });
      for (const offer of response.offers) await processOffer(offer, session, ended || current !== generation());
      for (const state of [...active.values()]) if (state.cleaned) await complete(state, 'failed', Object.create(null));
    } catch {
      for (const state of active.values()) if (state.window) void state.window.cancel();
    } finally { if (!ended) timer = setTimeout(() => { polling = poll(); }, 500); }
  }
  function invalidate() { for (const state of active.values()) if (state.window) void state.window.cancel(); }
  return Object.freeze({ start: async () => { if (!polling) polling = poll(); }, invalidate,
    dispose: async () => {
      ended = true; stopping.abort(); clearTimeout(timer); invalidate(); await polling;
      await Promise.all([...active.values()].filter(state => state.window).map(state => state.window.cancel()));
    } });
}
module.exports = { installLoginBridge };
