import { integerValue, objectValue, type ControlClient } from '../control/client';
import { createIdentity } from '../control/identity';
import { desktop } from '../app/desktop';

function waitFor(worker: Worker, expected: string, timeout: number): Promise<void> {
  return new Promise((resolve, reject) => {
    const signal = AbortSignal.timeout(timeout);
    const owner = new AbortController();
    const finish = (success: boolean) => {
      owner.abort();
      if (success) resolve();
      else reject(new Error('host_restart_observer_unavailable'));
    };
    const options = { once: true, signal: owner.signal };
    worker.addEventListener('message', event => finish(event.data === expected), options);
    worker.addEventListener('error', () => finish(false), options);
    signal.addEventListener('abort', () => finish(false), options);
  });
}

function waitForDesktopRefresh(): Promise<void> {
  return new Promise((resolve, reject) => {
    const signal = AbortSignal.timeout(50000);
    window.addEventListener('pagehide', () => resolve(), { once: true, signal });
    signal.addEventListener('abort', () => reject(new Error('host_restart_observer_unavailable')), { once: true });
  });
}

export async function restartService(client: ControlClient, instanceId: string, accepted: () => void): Promise<void> {
  const worker = desktop ? undefined : new Worker('/control/v1/maintenance/restart-observer.js');
  try {
    if (worker) await waitFor(worker, 'listening', 5000);
    const operationId = createIdentity();
    await client.restartHost(operationId, input => {
      const value = objectValue(input);
      const port = integerValue(value.port);
      const processId = integerValue(value.processId);
      if (value.state !== 'restarting' || value.operationId !== operationId || value.instanceId !== instanceId ||
          port < 1 || port > 65535 || processId < 1) throw new Error('host_restart_response_invalid');
      return { operationId, instanceId, port, processId };
    }, async reply => {
      accepted();
      if (!worker) return await waitForDesktopRefresh();
      const ready = waitFor(worker, 'ready', 50000);
      worker.postMessage(reply);
      await ready;
      const url = new URL(location.href);
      url.port = String(reply.port);
      if (url.href === location.href) location.reload();
      else location.replace(url);
    });
  } finally {
    worker?.terminate();
  }
}
