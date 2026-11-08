'use strict';
const net = require('node:net');
const { strictJson } = require('./strict-json.cjs');

function connectStartup(config) {
  if (!/^NexusDesktop-[a-f0-9]{32}$/.test(config.startupPipe) || !/^[a-f0-9]{32}$/.test(config.nonce))
    throw new Error('desktop_startup_pipe_invalid');
  const listeners = new Set(), requests = new Map();
  let snapshot = { kind: 'stage', stage: 0, total: 12, elapsedMilliseconds: 0 };
  let sequence = 0, ended = false, failure, activated = false;
  let resolveReady, rejectReady, resolveComplete, rejectComplete;
  const complete = new Promise((resolve, reject) => { resolveComplete = resolve; rejectComplete = reject; });
  complete.catch(() => {});
  const ready = new Promise((resolve, reject) => { resolveReady = resolve; rejectReady = reject; });
  ready.catch(() => {});
  const socket = net.createConnection('\\\\.\\pipe\\' + config.startupPipe);
  let pending = Buffer.alloc(0);
  function send(value) {
    if (ended) throw new Error('desktop_startup_channel_ended');
    socket.write(JSON.stringify(value) + '\n');
  }
  function fail(error) {
    if (ended) return;
    ended = true; rejectReady(error); rejectComplete(error);
    for (const request of requests.values()) request.reject(error);
    requests.clear();
    if (!failure) {
      snapshot = { kind: 'failed', reason: error.message, errorCode: 0 };
      for (const listener of listeners) listener(snapshot);
    }
  }
  socket.on('connect', () => send({ kind: 'hello', nonce: config.nonce }));
  socket.on('error', () => fail(new Error('desktop_startup_channel_unavailable')));
  socket.on('close', () => fail(new Error('desktop_host_ended')));
  socket.on('data', chunk => {
    try {
      pending = Buffer.concat([pending, chunk]);
      let end;
      while ((end = pending.indexOf(10)) >= 0) {
        if (end > 16384) throw new Error('desktop_startup_frame_limit');
        const value = strictJson(new TextDecoder('utf-8', { fatal: true }).decode(pending.subarray(0, end)));
        pending = pending.subarray(end + 1);
        if (value.kind === 'menu') {
          if (!Number.isInteger(value.action) || value.action < 0 || value.action > 3) throw new Error('desktop_menu_invalid');
          requests.get(value.id)?.resolve(value.action); requests.delete(value.id);
        } else if (value.kind === 'ready') resolveReady(value.config);
        else if (value.kind === 'complete') resolveComplete();
        else if (value.kind === 'stage' || value.kind === 'failed' || value.kind === 'activate') {
          if (value.kind === 'stage' && (!Number.isInteger(value.stage) || value.stage < 0 || value.stage >= 12 || value.total !== 12))
            throw new Error('desktop_startup_stage_invalid');
          if (value.kind === 'stage' && (snapshot.kind === 'failed' || value.stage < snapshot.stage)) continue;
          if (value.kind === 'activate') activated = true;
          if (value.kind === 'stage' && (snapshot.kind === 'failed' || value.stage < snapshot.stage)) continue;
          if (value.kind === 'activate') activated = true;
          if (value.kind === 'failed') { failure = value; rejectReady(new Error('desktop_startup_failed')); rejectComplete(new Error('desktop_startup_failed')); }
          if (value.kind !== 'activate') snapshot = value;
          for (const listener of listeners) listener(value);
        } else throw new Error('desktop_startup_message_invalid');
      }
      if (pending.length > 16384) throw new Error('desktop_startup_frame_limit');
    } catch { fail(new Error('desktop_startup_message_rejected')); socket.destroy(); }
  });
  return {
    ready, complete, get activated() { return activated; }, get failed() { return snapshot.kind === 'failed'; },
    get snapshot() { return snapshot; },
    subscribe(listener) { listeners.add(listener); listener(snapshot); return () => listeners.delete(listener); },
    menu(browser, busy, locale) {
      if (requests.size) return Promise.resolve(0);
      return new Promise((resolve, reject) => { const id = ++sequence; requests.set(id, { resolve, reject });
        try { send({ kind: 'menu', id, browser, busy, locale }); } catch (error) { requests.delete(id); reject(error); } });
    },
    presented: surface => send({ kind: 'presented', surface }),
    measure: (stage, elapsedMilliseconds) => send({ kind: 'measurement', stage, elapsedMilliseconds }),
    dismiss() { if (!ended) send({ kind: 'dismiss' }); },
    dispose() { ended = true; socket.destroy(); listeners.clear(); }
  };
}
module.exports = { connectStartup };
