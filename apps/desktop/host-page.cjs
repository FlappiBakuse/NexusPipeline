'use strict';

const retryable = new Set(['ERR_CONNECTION_REFUSED', 'ERR_CONNECTION_RESET', 'ERR_CONNECTION_CLOSED', 'ERR_EMPTY_RESPONSE']);

async function loadHostPage(window, page) {
  const deadline = performance.now() + 30000;
  while (!window.isDestroyed()) {
    const remaining = deadline - performance.now();
    if (remaining <= 0) throw new Error('desktop_host_startup_unavailable');
    let timer;
    try {
      await Promise.race([
        window.loadURL(page),
        new Promise((_resolve, reject) => {
          timer = setTimeout(() => reject(new Error('desktop_host_startup_unavailable')), remaining);
        })
      ]);
      return;
    } catch (error) {
      if (!retryable.has(error?.code) || window.isDestroyed()) throw error;
    } finally {
      clearTimeout(timer);
    }
    await new Promise(resolve => setTimeout(resolve, Math.min(100, Math.max(0, deadline - performance.now()))));
  }
  throw new Error('desktop_window_ended');
}

module.exports = { loadHostPage };
