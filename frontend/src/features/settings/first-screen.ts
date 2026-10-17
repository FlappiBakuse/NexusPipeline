import { api } from '../../platform/api';

export function loadSettingsPage(signal?: AbortSignal) {
  return api<any>('GET', '/api/settings', undefined, signal);
}
