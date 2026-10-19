import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, describe, expect, it, vi } from 'vitest';
import HistoryPage from './HistoryPage.vue';
import HistoryList from './components/HistoryList.vue';
import HistoryDetail from './components/HistoryDetail.vue';
import { historyUserKey } from './utils/historyFormat';
import { api } from '../../platform/api';
import type { HistoryRecord } from './utils/historyTypes';

const prepared = vi.hoisted(() => ({ data: undefined as unknown }));
vi.mock('../../platform/navigation-preparation', () => ({ takePreparedData: () => prepared.data }));
vi.mock('../../platform/api', () => ({ api: vi.fn(), isAbortError: () => false }));
vi.mock('../../platform/shell', () => ({ setTopbarTitle: vi.fn() }));
vi.mock('@bridge/index', () => ({ disposePluginSlot: vi.fn(async () => {}), renderPluginSlot: vi.fn(async () => {}) }));

afterEach(() => { prepared.data = undefined; window.location.hash = ''; vi.clearAllMocks(); });

describe('history linked user selection', () => {
  it.each(['prepared', 'mounted'])('queries the stable historical identity after closing a %s deep link', async mode => {
    const record: HistoryRecord = { id: 'run', userId: 'account-123', userName: 'Shared display name', startTime: '2026-10-18T00:00:00Z' };
    const day = '2026-10-18';
    window.location.hash = '#/history?recordId=run';
    if (mode === 'prepared') prepared.data = { from: day, to: day, dates: [{ date: day, count: 1 }], users: [], unsupportedCount: 0, record };
    vi.mocked(api).mockImplementation(async (_method, path) => {
      const url = new URL(path, 'http://localhost');
      if (url.pathname === '/api/history/detail') return { record };
      if (url.pathname === '/api/history') {
        expect(url.searchParams.get('userKey')).toBe('id:account-123');
        expect(url.searchParams.get('date')).toBe(day);
        return { records: [record] };
      }
      throw new Error(`Unexpected request: ${path}`);
    });
    const wrapper = mount(HistoryPage, { global: { stubs: { HistoryList: true, HistoryDetail: true, NxpPageHeader: true, RangePicker: true, NxpSelect: true } } });
    try {
      await flushPromises();
      const detail = wrapper.findComponent(HistoryDetail);
      expect(detail.props('detail')).toEqual(record);
      detail.vm.$emit('closeDetail');
      await flushPromises();
      expect(detail.props('detail')).toBeNull();
      const list = wrapper.findComponent(HistoryList);
      const user = list.props('usersByDate').get(day)![0]!;
      expect(user.userKey).toBe('id:account-123');
      list.vm.$emit('chooseUser', day, user);
      await flushPromises();
      expect(detail.props('error')).toBe('');
      expect(detail.props('records')).toEqual([record]);
      expect(vi.mocked(api).mock.calls.filter(([, path]) => new URL(path, 'http://localhost').pathname === '/api/history')).toHaveLength(1);
    } finally { wrapper.unmount(); }
  });

  it('preserves historical keys and never substitutes a display name for a missing identity', () => {
    expect(historyUserKey({ userKey: 'id:deleted-account', userId: 'current-account', userName: 'Renamed account' })).toBe('id:deleted-account');
    expect(historyUserKey({ userId: ' account-123 ', userName: 'Other account' })).toBe('id:account-123');
    expect(historyUserKey({ userName: 'Shared display name' })).toBe('');
  });
});
