import { api } from '../../platform/api';

export function loadUsersPage(signal?: AbortSignal) {
  return Promise.all([
    api<any>('GET', '/api/users', undefined, signal), api<any>('GET', '/api/scripts', undefined, signal),
    api<any>('GET', '/api/status', undefined, signal), api<any>('GET', '/api/plugin-contributions/user-list-badges', undefined, signal),
    api<any>('GET', '/api/users/task-summaries', undefined, signal),
  ]);
}
