import { createIdentity } from '../control/identity';
import { computed, ref, type Ref } from 'vue';
import type { AutomationStore } from '../app/store-ports';
import { initialValue, schemaAt } from '../control/schema';
import type { AutomationDraft } from '../app/automation-draft';
import type { AutomationBundle } from '../control/wire.generated';
import { copyDraft } from '../app/draft-copy';
import { accountDraft, accountKey, clearAccountDraft, canCancelAccount } from './account-draft';
import { hookPathKey } from './hook-paths';
import { scriptPathKey, scriptPaths } from './script-paths';
import { templateKey } from './script-template';
import { scriptSourceKey } from './script-source';
import type { AutomationSession } from './automation-session';

export type EntityKind = 'scripts' | 'users' | 'queues';
export const entityTitles: Record<EntityKind, string> = { scripts: '脚本', users: '账号', queues: '队列' };
interface QueueDraft {
  items: AutomationBundle['schedules'];
  baseline: AutomationBundle['schedules'];
  catalogRevision: number;
  revisions: Record<string, number>;
}

export class AutomationEditor {
  readonly instance: string | undefined;
  readonly activeKind: Ref<EntityKind>;
  readonly selected = ref('');
  readonly editing = ref(false);
  readonly userGlobal = ref(false);
  readonly catalog = computed(() => this.store.state.automation);
  readonly bundle = computed(() => this.catalog.value?.bundle);
  readonly rows = computed(() =>
    [...(this.bundle.value?.[this.activeKind.value] || [])].sort((a, b) => (a.order || 0) - (b.order || 0))
  );
  readonly draftKey = computed(
    () =>
      'automation/' +
      this.instance +
      '/' +
      this.kind() +
      '/' +
      this.activeKind.value +
      '/' +
      this.selected.value
  );
  readonly draft = computed({
    get: (): AutomationDraft => this.store.drafts.get(this.draftKey.value) as AutomationDraft || {},
    set: (value) => {
      this.store.drafts.set(this.draftKey.value, { ...value });
      this.store.dirty.add(this.draftKey.value);
    }
  });
  private readonly queueKey = computed(() => this.draftKey.value + '/schedules');
  readonly queueState = computed(
    () => this.store.drafts.get(this.queueKey.value) as QueueDraft | undefined
  );
  readonly queueSchedules = computed({
    get: () => this.queueState.value?.items || [],
    set: (items) => {
      if (this.queueState.value) this.queueState.value.items = items;
      this.store.dirty.add(this.draftKey.value);
    }
  });
  readonly accountBindings = computed({
    get: () => accountDraft(this.store, this.selected.value).items,
    set: (items) => {
      accountDraft(this.store, this.selected.value).items = items;
      this.store.dirty.add(this.draftKey.value);
    }
  });
  readonly createLabel = computed(() =>
    this.activeKind.value === 'users'
      ? '添加用户'
      : this.activeKind.value === 'scripts'
        ? '新建脚本实例'
        : '新建调度队列'
  );

  constructor(
    private readonly store: AutomationStore,
    private readonly kind: () => EntityKind,
    private readonly session: AutomationSession
  ) {
    this.instance = store.state.capabilities?.instanceId;
    this.activeKind = ref(kind());
  }

  current(): boolean {
    return this.instance === this.store.state.capabilities?.instanceId;
  }
  revision(id: string): number {
    return (
      this.catalog.value?.revisions.find(
        (item) => item.kind === this.activeKind.value.slice(0, -1) && item.id === id
      )?.revision || 0
    );
  }
  get unavailable(): boolean {
    return (
      this.editing.value &&
      this.activeKind.value === 'scripts' &&
      this.draft.value.kind === 'specialized' &&
      !scriptPaths(
        this.store,
        this.draft.value as AutomationBundle['scripts'][number]
      ).executable.trim()
    );
  }

  edit(row?: unknown, extensionId?: string): void {
    if (!this.catalog.value || this.session.saving || !this.current()) return;
    const schema = schemaAt(
      this.activeKind.value === 'scripts'
        ? 'automation-script.schema.json#/$defs/script'
        : 'automation-relations.schema.json#/$defs/' + this.activeKind.value.slice(0, -1)
    );
    const value = (row ? copyDraft(row) : initialValue(schema)) as AutomationDraft;
    if (!row) {
      const slot = this.activeKind.value === 'scripts' ? 'scripts/' + (extensionId || '') : this.activeKind.value;
      const slots = this.session.newDrafts ??= {};
      value.id = slots[slot] ??= createIdentity();
      value.order = this.rows.value.length;
      if (this.activeKind.value === 'queues') value.scheduleEnabled = false;
      if (this.activeKind.value === 'scripts') {
        value.limits = { maximumAttempts: 3, stallTimeoutSeconds: 300, totalTimeoutSeconds: 7200 };
        value.updateConfigurationAtFinish = true;
        if (extensionId) { value.kind = 'specialized'; value.extensionId = extensionId; }
      }
    }
    this.selected.value = String(value.id);
    const fresh = !this.store.drafts.has(this.draftKey.value);
    if (fresh) {
      this.store.drafts.set(this.draftKey.value, { ...value });
      this.session.revisions[this.draftKey.value] = row ? this.revision(String(value.id)) : 0;
      this.session.catalogs[this.draftKey.value] = this.catalog.value.catalogRevision;
    }
    if (this.activeKind.value === 'queues' && !this.queueState.value) this.initializeSchedules(!!row);
    if (fresh) this.formBaseline().value = this.formSnapshot();
    this.editing.value = true;
    this.session.error = '';
    this.session.notice = '';
  }

  private formBaseline() {
    return this.store.editors.target(this.instance || '', this.activeKind.value, this.selected.value)
      .child('host-form').retain('baseline', () => ({ value: '' }));
  }
  private formSnapshot(): string {
    return JSON.stringify([this.draft.value, this.activeKind.value === 'queues' ? this.queueState.value?.items : null,
      this.activeKind.value === 'users' ? this.accountBindings.value : null]);
  }
  get edited(): boolean {
    return this.store.drafts.has(this.draftKey.value) &&
      (this.formSnapshot() !== this.formBaseline().value ||
       this.store.dirty.has(scriptPathKey(this.selected.value)));
  }
  releasePristine(): void {
    if (this.session.saving || this.session.pending || this.edited) return;
    const key = this.draftKey.value;
    this.store.drafts.delete(key); this.store.drafts.delete(this.queueKey.value);
    if (this.activeKind.value === 'users') this.store.drafts.delete(accountKey(this.store, this.selected.value));
    this.store.dirty.delete(key); delete this.session.revisions[key]; delete this.session.catalogs[key];
    this.store.editors.target(this.instance || '', this.activeKind.value, this.selected.value).child('host-form').settle('disposed');
  }
  createScript(extensionId?: string): void {
    if (!this.current() || this.session.saving || this.session.pending || !this.catalog.value) return;
    this.edit(undefined, extensionId);
  }

  resumeNew(): boolean {
    if (!this.current() || this.session.saving || this.session.pending || !this.selected.value ||
        this.rows.value.some(row => row.id === this.selected.value) || !this.store.drafts.has(this.draftKey.value))
      return false;
    this.editing.value = true;
    this.session.error = '';
    return true;
  }

  private initializeSchedules(existing: boolean): void {
    const catalog = this.catalog.value!;
    const baseline = copyDraft(
      catalog.bundle.schedules.filter((item) => item.queueId === this.selected.value)
    );
    const items =
      baseline.length || existing
        ? copyDraft(baseline)
        : [
            {
              id: createIdentity(),
              queueId: this.selected.value,
              enabled: true,
              weekdays: [1, 2, 3, 4, 5],
              localMinute: 330,
              timeZoneId: Intl.DateTimeFormat().resolvedOptions().timeZone,
              missedPolicy: 'skip'
            }
          ];
    this.store.drafts.set(this.queueKey.value, {
      items,
      baseline,
      catalogRevision: catalog.catalogRevision,
      revisions: Object.fromEntries(
        baseline.map((item) => [
          item.id,
          catalog.revisions.find((value) => value.kind === 'schedule' && value.id === item.id)?.revision || 0
        ])
      )
    });
  }

  discard(): void {
    if (this.session.saving || this.session.pending) return;
    const id = this.selected.value,
      key = this.draftKey.value;
    if (!this.store.editors.target(this.instance || '', this.activeKind.value, id).canCancel() ||
        this.activeKind.value === 'users' && !canCancelAccount(this.store, id)) {
      this.session.error = '原操作尚未完成，请保留草稿并稍后查询。';
      return;
    }
    this.store.drafts.delete(key);
    this.store.dirty.delete(key);
    this.store.dirty.delete(scriptPathKey(id));
    delete this.session.revisions[key];
    delete this.session.catalogs[key];
    if (this.activeKind.value === 'queues') this.store.drafts.delete(this.queueKey.value);
    if (this.activeKind.value === 'users') clearAccountDraft(this.store, id);
    this.store.editors.settle(this.instance || '', this.activeKind.value, id, 'cancel');
    for (const [slot, value] of Object.entries(this.session.newDrafts || {}))
      if (value === id) delete this.session.newDrafts![slot];
    this.store.drafts.delete(hookPathKey(this.store, id));
    if (this.activeKind.value === 'scripts') this.store.drafts.delete(scriptPathKey(id));
    this.store.drafts.delete(templateKey(id));
    this.store.drafts.delete(scriptSourceKey(id));
    this.editing.value = false;
    this.selected.value = '';
    this.session.error = '';
    this.session.notice = '';
  }
}
