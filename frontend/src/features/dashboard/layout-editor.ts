export interface DashboardCard {
  cardId: string;
  sourceKind: "core" | "plugin";
  pluginName: string | null;
  localId: string;
  title: string;
  description: string;
  defaultVisible: boolean;
  defaultOrder: number;
}
export interface DashboardEntry { cardId: string; visible: boolean }
export interface DashboardSnapshot {
  schemaVersion: 1;
  catalogRevision: string;
  cards: DashboardCard[];
  layout: { schemaVersion: 1; layoutId: string; revision: number; updatedAtUtc: string; entries: DashboardEntry[] };
  visibleCardIds: string[];
}
export interface DashboardRead { data: DashboardSnapshot; etag: string | null }
export interface DashboardTransport {
  read(signal: AbortSignal): Promise<DashboardRead>;
  write(baseline: DashboardRead, ids: string[], signal: AbortSignal): Promise<DashboardSnapshot>;
}

function affectedEntries(snapshot: DashboardSnapshot, selected: string[]): DashboardEntry[] {
  const available = new Set(snapshot.cards.map(card => card.cardId));
  const chosen = new Set(selected);
  return selected.map(cardId => ({ cardId, visible: true })).concat(snapshot.layout.entries
    .filter(entry => available.has(entry.cardId) && !chosen.has(entry.cardId)).map(entry => ({ ...entry, visible: false })));
}

export class DashboardLayoutEditor {
  committed: DashboardSnapshot | null = null;
  etag: string | null = null;
  baseline: DashboardRead | null = null;
  draft: string[] = [];
  editing = false;
  conflict = false;
  pending = false;
  saving = false;
  reading = false;
  connected = true;
  error = "";
  removed: string[] = [];
  added: string[] = [];
  removedLabels: string[] = [];
  addedLabels: string[] = [];
  private controller: AbortController | null = null;
  private reader: Promise<void> | null = null;
  private reread = false;
  private disposed = false;
  private editingVisibleIds: string[] = [];
  private unknownTarget: { ids: Set<string>; entries: DashboardEntry[] } | null = null;

  constructor(private readonly transport: DashboardTransport) {}
  get visibleIds(): string[] {
    const available = new Set(this.committed?.cards.map(card => card.cardId) ?? []);
    return (this.editing ? this.editingVisibleIds : this.committed?.visibleCardIds ?? []).filter(id => available.has(id));
  }
  get canSave(): boolean {
    return this.editing && !this.saving && !this.reading && !this.conflict && !this.pending && this.connected && Boolean(this.baseline?.etag);
  }

  async refresh(): Promise<void> {
    if (this.disposed) return;
    if (this.saving) { this.reread = true; return; }
    if (this.reader) { this.reread = true; return this.reader; }
    this.reader = this.readLatest();
    try { await this.reader; } finally { this.reader = null; }
    if (this.reread) { this.reread = false; await this.refresh(); }
  }

  private async readLatest(): Promise<void> {
    this.reading = true;
    const request = new AbortController(); this.controller = request;
    const timeout = setTimeout(() => request.abort(), 15000);
    try {
      const result = await this.transport.read(request.signal);
      if (this.disposed || request.signal.aborted) return;
      if (!result.etag || !/^"dashboard-sha256-[0-9a-f]{64}"$/.test(result.etag)) throw new Error("dashboard_layout_unavailable");
      this.committed = result.data; this.etag = result.etag; this.connected = true; this.error = "";
      if (this.unknownTarget) {
        const actual = result.data.layout.entries.filter(entry => this.unknownTarget!.ids.has(entry.cardId));
        const matches = JSON.stringify(actual) === JSON.stringify(this.unknownTarget.entries);
        this.unknownTarget = null; this.pending = false;
        if (matches) this.finishEditing();
        else { this.conflict = true; this.error = "dashboard_layout_conflict"; }
      } else if (this.editing && this.baseline?.etag !== result.etag) this.conflict = true;
    } catch (reason) {
      if (!this.disposed) { this.connected = false; this.error = (reason as { code?: string })?.code || "dashboard_layout_read_failed"; }
    } finally {
      clearTimeout(timeout);
      if (this.controller === request) this.controller = null;
      this.reading = false;
    }
  }

  begin(defaults = false): void {
    if (!this.committed || !this.etag || this.saving || this.pending) return;
    this.baseline = { data: this.committed, etag: this.etag };
    this.draft = [...this.committed.visibleCardIds]; this.editingVisibleIds = [...this.draft]; this.editing = true; this.conflict = false;
    this.error = ""; this.removed = []; this.added = []; this.removedLabels = []; this.addedLabels = [];
    if (defaults) this.defaults();
  }
  defaults(): void {
    if (this.saving || !this.committed) return;
    this.draft = this.committed.cards.filter(card => card.defaultVisible).map(card => card.cardId);
  }
  select(ids: string[]): void {
    if (this.saving || this.pending) return;
    const available = new Set(this.committed?.cards.map(card => card.cardId));
    this.draft = [...new Set(ids)].filter(id => available.has(id));
  }
  cancel(): void { if (!this.saving) { this.finishEditing(); this.unknownTarget = null; this.pending = false; } }
  private finishEditing(): void {
    this.editing = false; this.baseline = null; this.draft = []; this.editingVisibleIds = []; this.conflict = false;
    this.removed = []; this.added = []; this.removedLabels = []; this.addedLabels = [];
  }
  loadLatest(): void {
    if (this.saving || this.pending || !this.connected) return;
    this.finishEditing(); this.begin();
  }
  rebase(): void {
    if (!this.committed || !this.etag || !this.connected || this.saving || this.pending) return;
    const cards = this.committed.cards;
    const available = new Set(cards.map(card => card.cardId));
    const known = new Set(this.baseline?.data.cards.map(card => card.cardId));
    this.removed = this.draft.filter(id => !available.has(id));
    this.added = cards.filter(card => !known.has(card.cardId) && card.defaultVisible).map(card => card.cardId);
    this.removedLabels = this.removed.map(id => this.baseline?.data.cards.find(card => card.cardId === id)?.title ?? id);
    this.addedLabels = this.added.map(id => cards.find(card => card.cardId === id)!.title);
    this.draft = [...new Set([...this.draft.filter(id => available.has(id)), ...this.added])];
    this.baseline = { data: this.committed, etag: this.etag }; this.conflict = false; this.error = "";
  }

  async save(): Promise<void> {
    if (!this.canSave || !this.baseline) return;
    const baseline = this.baseline, ids = [...this.draft];
    this.saving = true; this.error = "";
    const request = new AbortController(); this.controller = request;
    const timeout = setTimeout(() => request.abort(), 15000);
    let readBack = false;
    try {
      const saved = await this.transport.write(baseline, ids, request.signal);
      if (this.disposed) return;
      this.committed = saved; this.etag = null; this.finishEditing(); readBack = true;
    } catch (reason) {
      if (this.disposed) return;
      const failure = reason as { status?: number; code?: string };
      if (failure.status === 409 || failure.status === 412) { this.conflict = true; readBack = true; }
      else if (!failure.status || failure.status >= 500 && !["dashboard_layout_persistence_failed", "dashboard_layout_unavailable"].includes(failure.code || "")) {
        this.pending = true;
        this.unknownTarget = { ids: new Set(baseline.data.cards.map(card => card.cardId)), entries: affectedEntries(baseline.data, ids) };
        readBack = true;
      }
      this.error = failure.code || "dashboard_layout_save_failed";
    } finally {
      clearTimeout(timeout);
      if (this.controller === request) this.controller = null;
      this.saving = false;
    }
    if (readBack || this.reread) { this.reread = false; await this.refresh(); }
  }
  invalidate(kind: string, data: Record<string, unknown>): void {
    if (kind === "layout" && this.committed && data.layoutId === this.committed.layout.layoutId && Number(data.revision) <= this.committed.layout.revision) return;
    if (kind === "catalog" && data.catalogRevision === this.committed?.catalogRevision) return;
    void this.refresh();
  }
  disconnect(): void { this.connected = false; }
  pause(): void { this.disconnect(); this.controller?.abort(); }
  dispose(): void { this.disposed = true; this.controller?.abort(); }
}
