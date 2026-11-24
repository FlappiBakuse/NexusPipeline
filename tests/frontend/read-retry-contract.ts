import { ControlClient, ControlFailure, objectValue } from '../../apps/web/src/control/client';
import { retryRead } from '../../apps/web/src/control/read-retry';

export async function readRetryContract(): Promise<string[]> {
  const checks: string[] = [];
  const check = (condition: boolean, message: string) => {
    if (!condition) throw new Error(message);
    checks.push(message);
  };
  const path = '/control/v1/notifications';
  const signal = new AbortController().signal;
  let requests = 0;
  const invalid = new ControlClient('http://retry.test', async () => {
    requests++;
    return Response.json('wrong shape');
  });
  const decoded = await retryRead(() => invalid.read(path, objectValue), signal).catch(error => error);
  check(decoded instanceof TypeError && requests === 1, 'a fixed decoder fault makes one read');

  for (const failure of [new ControlFailure(409, 'resource_unavailable'),
    new ControlFailure(500, 'control_request_failed'), new ControlFailure(503, 'write_outcome_unknown', undefined, 'operation', true)]) {
    requests = 0;
    const observed = await retryRead(async () => { requests++; throw failure; }, signal).catch(error => error);
    check(observed === failure && requests === 1, 'terminal read policy preserves ' + failure.code);
  }

  requests = 0;
  const disconnected = new ControlClient('http://retry.test', async () => {
    if (++requests === 1) throw new TypeError('Failed to fetch');
    return Response.json({ recovered: true });
  });
  const result = await retryRead(() => disconnected.read(path, objectValue), signal);
  check(result.recovered === true && requests === 2, 'a real fetch rejection can resume a repeatable read');

  requests = 0;
  const streamFault = new ControlClient('http://retry.test', async () => {
    if (++requests === 1) return new Response(new ReadableStream({
      start(controller) { controller.error(new TypeError('network stream failed')); }
    }));
    return Response.json({ recovered: true });
  });
  check((await retryRead(() => streamFault.read(path, objectValue), signal)).recovered === true && requests === 2,
    'response stream failure is classified at the transport boundary');

  const owner = new AbortController();
  requests = 0;
  const abortReason = new Error('stop waiting');
  const pending = retryRead(async () => { requests++; throw new ControlFailure(429, 'rate_limited'); }, owner.signal);
  await Promise.resolve();
  owner.abort(abortReason);
  check(await pending.catch(error => error) === abortReason && requests === 1,
    'cancellation during backoff preserves the reason and starts no new read');
  return checks;
}
