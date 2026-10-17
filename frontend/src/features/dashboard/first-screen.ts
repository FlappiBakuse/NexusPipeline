import { api, apiResponse } from '../../platform/api';
import { getLocale } from '../../platform/i18n';
import type { DashboardSnapshot } from './layout-editor';

export async function loadDashboardPage(signal?: AbortSignal) {
  const [status, layout] = await Promise.all([
    api<any>('GET', '/api/status', undefined, signal),
    apiResponse<DashboardSnapshot>('GET', '/api/dashboard/layout', undefined, signal, { 'Accept-Language': getLocale() }),
  ]);
  if (!layout.etag || !/^"dashboard-sha256-[0-9a-f]{64}"$/.test(layout.etag)) throw new Error('dashboard_layout_unavailable');
  return { status, layout };
}
