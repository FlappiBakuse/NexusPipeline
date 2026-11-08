import { ControlFailure } from './client';

export async function retryRead<T>(read: () => Promise<T>, signal: AbortSignal): Promise<T> {
  const delays = [1000, 3000, 10000];
  for (let attempt = 0; ; attempt++) {
    signal.throwIfAborted();
    try { return await read(); }
    catch (error) {
      const transient = error instanceof TypeError || error instanceof Error && error.message === 'prepared_read_revoked' ||
        (error instanceof ControlFailure && (error.status >= 500 || [408, 429].includes(error.status)));
      if (signal.aborted || !transient || attempt >= delays.length) throw error;
      await new Promise<void>((resolve, reject) => {
        const cancel = () => { clearTimeout(timer); reject(signal.reason); };
        const timer = setTimeout(() => { signal.removeEventListener('abort', cancel); resolve(); }, delays[attempt]);
        signal.addEventListener('abort', cancel, { once: true });
      });
    }
  }
}
