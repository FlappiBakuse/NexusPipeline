import { ControlClient } from '../../apps/web/src/control/client.js';
import { LiveLogs, type LogIdentity } from '../../apps/web/src/control/live-logs.js';

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
  return checks;
}
