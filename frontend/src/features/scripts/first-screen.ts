import { api } from '../../platform/api';

export function loadScriptsPage(signal?: AbortSignal) {
  return Promise.all([api<any>('GET', '/api/scripts', undefined, signal), api<any>('GET', '/api/status', undefined, signal)]);
}
