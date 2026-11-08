import { ref } from 'vue';
import type { NavigationStore } from '../app/store-ports';
import type { RecordRow } from '../control/models';
import { recordRow } from '../control/records';

export class HistoryLink {
  readonly selected = ref<RecordRow>();
  readonly detailOpen = ref(false);
  readonly attemptId = ref('');
  readonly deepLinkError = ref('');
  private owner?: AbortController;
  private applied = '';
  private readonly instance: string | undefined;

  constructor(
    private readonly store: NavigationStore,
    private readonly visible: () => boolean,
    private readonly selectRows: (record: RecordRow) => Promise<void>,
    private readonly abortSummary: () => void
  ) {
    this.instance = store.state.capabilities?.instanceId;
    window.addEventListener('hashchange', this.locationChanged);
  }

  abort(): void {
    this.owner?.abort();
  }
  dispose(): void {
    this.abort();
    window.removeEventListener('hashchange', this.locationChanged);
  }

  select(row: RecordRow, attempt = '', fromLink = false): void {
    if (!fromLink) this.abort();
    this.selected.value = row;
    this.attemptId.value = attempt;
    this.detailOpen.value = true;
    history.replaceState(
      null,
      '',
      this.path(row, attempt)
    );
    if (!fromLink) this.applied = this.key(row.runId, row.userId || '', attempt);
  }

  clear(): void {
    this.abort();
    this.detailOpen.value = false;
    this.selected.value = undefined;
    this.attemptId.value = '';
    this.applied = '';
    this.deepLinkError.value = '';
    if (this.store.state.page === 'history') history.replaceState(null, '', '#/history');
  }

  chooseAttempt(id: string): void {
    if (!this.selected.value) return;
    this.abort();
    this.attemptId.value = id;
    history.replaceState(
      null,
      '',
      this.path(this.selected.value, id)
    );
    this.applied = this.key(this.selected.value.runId, this.selected.value.userId || '', id);
  }

  async restore(): Promise<void> {
    if (!this.visible() || this.store.state.page !== 'history') return;
    this.abort();
    if (!location.hash.startsWith('#/history?')) {
      this.applied = '';
      this.deepLinkError.value = '';
      return;
    }
    const query = new URLSearchParams(location.hash.split('?')[1] || '');
    const runId = query.get('run') || '',
      candidate = query.get('attempt') || '',
      user = query.get('user') || '';
    const attempt = this.validId(candidate) ? candidate : '';
    if (!this.validId(runId) || query.has('user') && !this.validId(user) || candidate && !this.validId(candidate)) {
      this.selected.value = undefined;
      this.detailOpen.value = false;
      this.deepLinkError.value = '记录链接无效或已不可用。';
      this.applied = '';
      return;
    }
    const key = this.key(runId, user, attempt);
    if (
      key === this.applied &&
      this.selected.value?.runId === runId &&
      (this.selected.value.userId || '') === user &&
      (!attempt || this.attemptId.value === attempt)
    )
      return;
    this.abortSummary();
    const owner = new AbortController();
    this.owner = owner;
    this.deepLinkError.value = '';
    try {
      const row = await this.store.client.read(
        '/control/v1/records/' + encodeURIComponent(runId) + (user ? '?userId=' + encodeURIComponent(user) : ''),
        value => recordRow(value, runId),
        owner.signal
      );
      owner.signal.throwIfAborted();
      if (user && row.userId !== user) throw new TypeError('history_account_unbound');
      if (this.instance !== this.store.state.capabilities?.instanceId) return;
      await this.selectRows(row);
      owner.signal.throwIfAborted();
      if (!this.visible() || this.store.state.page !== 'history') return;
      this.select(row, attempt, true);
      if (location.hash.startsWith('#/history?run=' + encodeURIComponent(runId))) this.applied = key;
    } catch (error) {
      if (!owner.signal.aborted)
        this.deepLinkError.value = error instanceof Error ? error.message : String(error);
    }
  }

  private key(runId: string, user: string, attempt: string): string {
    return runId + ':' + user + ':' + attempt;
  }

  private path(row: RecordRow, attempt: string): string {
    return '#/history?run=' + encodeURIComponent(row.runId) +
      (row.userId ? '&user=' + encodeURIComponent(row.userId) : '') +
      (attempt ? '&attempt=' + encodeURIComponent(attempt) : '');
  }

  private validId(value: string): boolean {
    return /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value);
  }
  private readonly locationChanged = (): void => {
    if (this.visible() && this.store.state.page === 'history') void this.restore();
  };
}
