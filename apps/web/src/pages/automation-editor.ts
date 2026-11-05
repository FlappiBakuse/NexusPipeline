import { createIdentity } from '../control/identity';
import { computed, ref, type Ref } from 'vue';
import type { AppStore } from '../app/store';
import { initialValue, schemaAt } from '../control/schema';
import type { AutomationBundle } from '../control/wire.generated';
import { copyDraft } from '../app/draft-copy';
import { accountDraft, clearAccountDraft } from './account-draft';
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
    get: () => this.store.drafts.get(this.draftKey.value) || {},
    set: (value) => {
      this.store.drafts.set(this.draftKey.value, value);
      this.store.dirty.add(this.draftKey.value);
    }
  });
  private readonly queueKey = computed(() => this.draftKey.value + '/schedules');
  readonly queueState = computed(
    () => this.store.drafts.get(this.queueKey.value) as unknown as QueueDraft | undefined
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
    private readonly store: AppStore,
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
        this.draft.value as unknown as AutomationBundle['scripts'][number]
      ).executable.trim()
    );
  }

  edit(row?: unknown): void {
    if (!this.catalog.value || this.session.saving || !this.current()) return;
    const schema = schemaAt(
      this.activeKind.value === 'scripts'
        ? 'automation-script.schema.json#/$defs/script'
        : 'automation-relations.schema.json#/$defs/' + this.activeKind.value.slice(0, -1)
    );
    const value = (row ? copyDraft(row) : initialValue(schema)) as Record<string, unknown>;
    if (!row) {
      value.id = createIdentity();
      value.order = this.rows.value.length;
      if (this.activeKind.value === 'queues') value.scheduleEnabled = false;
      if (this.activeKind.value === 'scripts') {
        value.limits = { maximumAttempts: 3, stallTimeoutSeconds: 300, totalTimeoutSeconds: 7200 };
        value.updateConfigurationAtFinish = true;
      }
    }
    this.selected.value = String(value.id);
    if (row && !this.store.dirty.has(this.draftKey.value) &&
        !this.store.dirty.has(scriptPathKey(this.selected.value))) {
      this.store.drafts.delete(this.draftKey.value);
      this.store.drafts.delete(this.queueKey.value);
      this.store.drafts.delete(scriptPathKey(this.selected.value));
    }
    if (!this.store.drafts.has(this.draftKey.value)) {
      this.store.drafts.set(this.draftKey.value, value);
      this.session.revisions[this.draftKey.value] = row ? this.revision(String(value.id)) : 0;
      this.session.catalogs[this.draftKey.value] = this.catalog.value.catalogRevision;
      if (!row) this.store.dirty.add(this.draftKey.value);
    }
    if (this.activeKind.value === 'queues' && !this.queueState.value) this.initializeSchedules(!!row);
    this.editing.value = true;
    this.session.error = '';
    this.session.notice = '';
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
    this.store.drafts.delete(key);
    this.store.dirty.delete(key);
    this.store.dirty.delete(scriptPathKey(id));
    delete this.session.revisions[key];
    delete this.session.catalogs[key];
    if (this.activeKind.value === 'queues') this.store.drafts.delete(this.queueKey.value);
    if (this.activeKind.value === 'users') clearAccountDraft(this.store, id);
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
