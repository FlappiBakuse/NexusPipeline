import { ControlClient } from '../../apps/web/src/control/client.js';
import { LiveLogs, type LogIdentity, type LogWindow } from '../../apps/web/src/control/live-logs.js';

export async function liveLogContract(): Promise<string[]> {
  const checks: string[] = [];
  const reads: { step: string; signal: AbortSignal }[] = [];
  const delivered = new Set<string>();
  let peak = 0;
  const waitFor = async (ready: () => boolean): Promise<void> => {
    const deadline = Date.now() + 6000;
    while (!ready()) {
      if (Date.now() > deadline) throw new Error('live_log_contract_timeout');
      await new Promise(resolve => setTimeout(resolve, 10));
    }
  };
  const check = (value: unknown, text: string): void => {
    if (!value) throw new Error(text);
    checks.push(text);
  };
  const client = new ControlClient('http://logs.test', async (input, init) => {
    const parts = new URL(String(input)).pathname.split('/');
    const key = { runId: parts[5]!, attemptId: parts[7]!, stepId: parts[9]!, role: parts[10]! };
    const signal = init!.signal as AbortSignal;
    reads.push({ step: key.stepId, signal });
    peak = Math.max(peak, reads.filter(read => !read.signal.aborted).length);
    return new Response(new ReadableStream<Uint8Array>({ start(controller) {
      controller.enqueue(new TextEncoder().encode('data: ' + JSON.stringify({ kind: 'tail', key,
        data: { lastSequence: 1, prefixOmitted: false, frames: [{ sequence: 1, bytes: btoa(key.stepId + '\n') }] } }) + '\n\n'));
      signal.addEventListener('abort', () => controller.error(new DOMException('Aborted', 'AbortError')), { once: true });
    } }), { headers: { 'Content-Type': 'text/event-stream' } });
  });
  const logs = new LiveLogs(client);
  const releases = Array.from({ length: 6 }, (_, index) => {
    const key: LogIdentity = { runId: 'run', attemptId: 'attempt', stepId: 'step-' + index, role: 'main' };
    return logs.subscribe(key, value => { if (value.text) delivered.add(value.text.trim()); });
  });
  try {
    await waitFor(() => delivered.size === 6);
    check(peak <= 4 && JSON.stringify([...delivered].sort()) ===
      JSON.stringify(['step-0', 'step-1', 'step-2', 'step-3', 'step-4', 'step-5']),
      'bounded readers deliver every visible log without requiring the concurrency ceiling to be saturated');
    const before = reads.length;
    releases[0]!(); releases[0]!();
    await new Promise(resolve => setTimeout(resolve, 10));
    check(reads.slice(before).every(read => read.step !== 'step-0'), 'releasing the same listener twice cannot restart its stream');
  } finally {
    releases.forEach(release => release());
  }
  await waitFor(() => reads.every(read => read.signal.aborted));
  const count = reads.length;
  await new Promise(resolve => setTimeout(resolve, 2100));
  check(reads.length === count, 'all active, waiting and rotation owners stop after the last listener leaves');
  let streams = 0, tails = 0, finalText = '';
  let activeSignal: AbortSignal | undefined;
  const finalKey: LogIdentity = { runId: 'run', attemptId: 'attempt', stepId: 'finished', role: 'main' };
  const complete = new LiveLogs(new ControlClient('http://logs.test', async (input, init) => {
    if (String(input).endsWith('/tail')) {
      tails++;
      return Response.json({ description: { key: finalKey, isComplete: true }, prefixOmitted: false,
        slices: [{ bytes: btoa('finished\n') }] });
    }
    streams++;
    const signal = init!.signal as AbortSignal;
    return new Response(new ReadableStream<Uint8Array>({ start(controller) {
      if (String(input).includes('/finished/')) { controller.close(); return; }
      activeSignal = signal;
      signal.addEventListener('abort', () => controller.error(new DOMException('Aborted', 'AbortError')), { once: true });
    } }), { headers: { 'Content-Type': 'text/event-stream' } });
  }));
  const releaseFinal = complete.subscribe(finalKey, value => { finalText = value.text; });
  let releaseActive: (() => void) | undefined;
  try {
    await waitFor(() => finalText === 'finished\n');
    releaseActive = complete.subscribe({ ...finalKey, stepId: 'active' }, () => {});
    await waitFor(() => activeSignal !== undefined);
    await new Promise(resolve => setTimeout(resolve, 2200));
    check(streams === 2 && tails === 1 && finalText === 'finished\n',
      'a complete durable tail stays visible without another archive read or stream reconnect');
    check(!activeSignal!.aborted, 'a retained complete log cannot rotate the only active stream');
  } finally { releaseFinal(); releaseActive?.(); }

  const badKey = { ...finalKey, stepId: 'bad-frame' };
  const recoverKey = { ...finalKey, stepId: 'reconnected' };
  let malformedReads = 0, recoveredReads = 0, errorText = '', recoveredText = '';
  const classified = new LiveLogs(new ControlClient('http://logs.test', async input => {
    const path = String(input);
    if (path.includes('/bad-frame/')) {
      malformedReads++;
      return new Response('data: {\n\n');
    }
    if (++recoveredReads === 1) throw new TypeError('Failed to fetch');
    const line = (kind: string, data: unknown) => 'data: ' + JSON.stringify({ kind, key: recoverKey, data }) + '\n\n';
    const chunks = [
      line('tail', { lastSequence: 1, prefixOmitted: false, frames: [{ sequence: 1, bytes: btoa('\xe2\x82') }] }),
      line('frame', { sequence: 2, bytes: btoa('\xac\n') })
    ];
    return new Response(new ReadableStream({ start(controller) {
      chunks.forEach(chunk => controller.enqueue(new TextEncoder().encode(chunk)));
    } }));
  }));
  const releaseBad = classified.subscribe(badKey, value => { errorText = value.error || ''; });
  const releaseRecovered = classified.subscribe(recoverKey, value => { recoveredText = value.text; });
  try {
    await waitFor(() => errorText.length > 0 && recoveredText === '€\n');
    await new Promise(resolve => setTimeout(resolve, 150));
    check(malformedReads === 1 && errorText === 'log_event_invalid',
      'invalid frames are visible and leave no reconnect scheduled');
    check(recoveredReads === 2, 'real transport failure reconnects and preserves UTF-8 across log frames');
  } finally { releaseBad(); releaseRecovered(); }
  const burstKey = { ...finalKey, stepId: 'burst' };
  let burstSequence = 0;
  const burst = new LiveLogs(new ControlClient('http://logs.test', async (_input, init) => {
    const line = (kind: string, data: unknown) => 'data: ' + JSON.stringify({ kind, key: burstKey, data }) + '\n\n';
    const frames = Array.from({ length: 300 }, (_, index) =>
      line('frame', { sequence: index + 1, bytes: btoa('line\n') }));
    const data = line('tail', { lastSequence: 0, prefixOmitted: false, frames: [] }) + frames.join('');
    return new Response(new ReadableStream({ start(controller) {
      controller.enqueue(new TextEncoder().encode(data));
      init!.signal!.addEventListener('abort', () => controller.close(), { once: true });
    } }));
  }));
  const releaseBurst = burst.subscribe(burstKey, value => { burstSequence = value.sequence; });
  try {
    await waitFor(() => burstSequence === 300);
    check(burstSequence === 300, 'a coalesced fetch chunk delivers all 300 valid frames across batches');
  } finally { releaseBurst(); }
  const gapKey = { ...finalKey, stepId: 'gap' };
  const resyncKey = { ...finalKey, stepId: 'resync' };
  const unmarkedKey = { ...finalKey, stepId: 'unmarked-gap' };
  const durableReads = new Map<string, number>();
  const recovered = new Map<string, LogWindow>();
  const resumable = new LiveLogs(new ControlClient('http://logs.test', async input => {
    const url = String(input);
    const key = [gapKey, resyncKey, unmarkedKey].find(candidate => url.includes('/' + candidate.stepId + '/'))!;
    if (url.endsWith('/tail')) {
      durableReads.set(key.stepId, (durableReads.get(key.stepId) ?? 0) + 1);
      return Response.json({ description: { key, isComplete: true }, prefixOmitted: true,
        slices: [{ bytes: btoa('saved \xf0\x9f') }, { bytes: btoa('\x98\x80\xe2\x82') }] });
    }
    const line = (kind: string, data: unknown) => 'data: ' + JSON.stringify({ kind, key, data }) + '\n\n';
    const interrupted = key === resyncKey ? line('resync', { reasonCode: 'log_rotated' }) :
      line('frame', { sequence: 3, gapBefore: key === gapKey, bytes: btoa('outside-tail') });
    return new Response(line('tail', { lastSequence: 1, prefixOmitted: false,
      frames: [{ sequence: 1, bytes: btoa('uncommitted-live') }] }) + interrupted);
  }));
  const stopResumable = [gapKey, resyncKey, unmarkedKey].map(key =>
    resumable.subscribe(key, value => { recovered.set(key.stepId, value); }));
  try {
    await waitFor(() => !recovered.get(gapKey.stepId)?.connected && !recovered.get(resyncKey.stepId)?.connected &&
      recovered.get(unmarkedKey.stepId)?.error === 'log_gap');
    for (const key of [gapKey, resyncKey]) {
      const window = recovered.get(key.stepId);
      check(durableReads.get(key.stepId) === 1 && window?.text === 'saved 😀�' &&
        window.omitted && window.sequence === 2 && !window.connected && !window.error,
        key.stepId + ' loads one complete durable tail and flushes its UTF-8 decoder');
    }
    check(!durableReads.has(unmarkedKey.stepId) && recovered.get(unmarkedKey.stepId)?.text === 'uncommitted-live',
      'an unmarked sequence mismatch remains a terminal control error');
  } finally { stopResumable.forEach(release => release()); }
  const boundedKey = { ...finalKey, stepId: 'bounded-tail' };
  const suffix = 'x'.repeat(256 * 1024 - 1);
  const blocks = ['\xf0\x9f', '\x98\x80', ...Array.from({ length: Math.ceil(suffix.length / 8192) },
    (_, index) => suffix.slice(index * 8192, (index + 1) * 8192))];
  const capped = new LiveLogs(new ControlClient('http://logs.test', async (_input, init) => {
    const line = (kind: string, data: unknown) => 'data: ' + JSON.stringify({ kind, key: boundedKey, data }) + '\n\n';
    const data = line('tail', { lastSequence: 0, prefixOmitted: false, frames: [] }) +
      blocks.map((block, index) => line('frame', { sequence: index + 1, bytes: btoa(block) })).join('');
    return new Response(new ReadableStream({ start(controller) {
      controller.enqueue(new TextEncoder().encode(data));
      init!.signal!.addEventListener('abort', () => controller.close(), { once: true });
    } }));
  }));
  let boundedWindow: LogWindow = { text: '', sequence: 0, omitted: false, connected: false };
  const releaseCapped = capped.subscribe(boundedKey, value => { boundedWindow = value; });
  try {
    await waitFor(() => boundedWindow.sequence === blocks.length);
    check(boundedWindow.text === suffix && boundedWindow.omitted,
      'the live tail never keeps half a Unicode code point at its character limit');
  } finally { releaseCapped(); }
  check(boundedWindow.text === suffix, 'releasing a reader preserves the last published log snapshot');
  return checks;
}
