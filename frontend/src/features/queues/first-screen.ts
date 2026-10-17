import { api } from '../../platform/api';

export function loadQueuesPage(signal?: AbortSignal) {
  return Promise.all([api<any>('GET', '/api/queues', undefined, signal), api<any>('GET', '/api/scripts', undefined, signal), api<any>('GET', '/api/status', undefined, signal)]);
}
