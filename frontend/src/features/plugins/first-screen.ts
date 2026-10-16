import { api } from '../../platform/api';

export function loadPluginsPage(signal?: AbortSignal) {
  return api<any[]>('GET', '/api/plugins', undefined, signal);
}
