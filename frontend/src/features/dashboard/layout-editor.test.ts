import { describe, expect, it, vi } from "vitest";
import { DashboardLayoutEditor, type DashboardRead, type DashboardSnapshot, type DashboardTransport } from "./layout-editor";

const ids = ["core:status", "core:running", "core:history-duration"];
function snapshot(revision = 1, visible = ids, extra: string[] = []): DashboardSnapshot {
  const all = [...ids, ...extra];
  return { schemaVersion: 1, catalogRevision: "a".repeat(64),
    cards: all.map((cardId, index) => ({ cardId, sourceKind: "core", pluginName: null, localId: cardId, title: cardId, description: "", defaultVisible: true, defaultOrder: index })),
    layout: { schemaVersion: 1, layoutId: "layout", revision, updatedAtUtc: "2026-10-10T00:00:00Z", entries: [...visible, ...all.filter(id => !visible.includes(id))].map(cardId => ({ cardId, visible: visible.includes(cardId) })) }, visibleCardIds: [...visible] };
}
function representation(data: DashboardSnapshot, generation = data.layout.revision): DashboardRead {
  return { data, etag: `"dashboard-sha256-${generation.toString(16).padStart(64, "0")}"` };
}
function fixture(initial = snapshot()) {
  let current = representation(initial);
  const read = vi.fn(async () => current);
  const write = vi.fn<DashboardTransport["write"]>(async (_baseline, selected) => {
    current = representation(snapshot(current.data.layout.revision + 1, selected)); return current.data;
  });
  const editor = new DashboardLayoutEditor({ read, write });
  return { editor, read, write, set: (data: DashboardSnapshot, generation?: number) => { current = representation(data, generation); } };
}
describe("dashboard layout transactions", () => {
  it("keeps drafts separate from the displayed commit and cancels to the latest confirmed commit", async () => {
    const f = fixture(); await f.editor.refresh(); f.editor.begin(); f.editor.select([ids[2]]);
    expect(f.editor.visibleIds).toEqual(ids); expect(f.editor.draft).toEqual([ids[2]]); expect(f.write).not.toHaveBeenCalled();
    f.set(snapshot(2, [ids[1]])); await f.editor.refresh();
    expect(f.editor.draft).toEqual([ids[2]]); expect(f.editor.conflict).toBe(true);
    expect(f.editor.visibleIds).toEqual(ids);
    f.editor.cancel(); expect(f.editor.visibleIds).toEqual([ids[1]]); f.editor.dispose();
  });
  it("freezes one save and gets a fresh validator after confirmed success", async () => {
    const f = fixture(); await f.editor.refresh(); f.editor.begin(); f.editor.select([]);
    let complete!: (data: DashboardSnapshot) => void;
    f.write.mockImplementationOnce(() => new Promise(resolve => { complete = resolve; }));
    const saving = f.editor.save(); f.editor.select(ids); await f.editor.save(); f.editor.cancel();
    expect(f.editor.draft).toEqual([]); expect(f.editor.editing).toBe(true); expect(f.write).toHaveBeenCalledTimes(1);
    f.set(snapshot(2, [])); complete(snapshot(2, [])); await saving;
    expect(f.editor.visibleIds).toEqual([]); expect(f.editor.editing).toBe(false); expect(f.read).toHaveBeenCalledTimes(2);
    f.editor.dispose();
  });
  it("preserves a rejected draft and requires an explicit rebase before another save", async () => {
    const f = fixture(); await f.editor.refresh(); f.editor.begin(); f.editor.select([ids[0]]);
    f.set(snapshot(2, [ids[2]])); f.write.mockRejectedValueOnce({ status: 412, code: "dashboard_layout_conflict" });
    await f.editor.save(); await f.editor.save();
    expect(f.editor.draft).toEqual([ids[0]]); expect(f.editor.committed?.visibleCardIds).toEqual([ids[2]]);
    expect(f.write).toHaveBeenCalledTimes(1); f.editor.rebase(); expect(f.editor.canSave).toBe(true); f.editor.dispose();
  });
  it("keeps valid choices and includes newly discovered default cards during rebase", async () => {
    const old = "plugin:old:card", fresh = "plugin:new:card";
    const initial = snapshot(1, [...ids, old], [old]); initial.cards.find(card => card.cardId === old)!.title = "Old activity";
    const f = fixture(initial); await f.editor.refresh(); f.editor.begin(); f.editor.select([old, ids[2]]);
    f.set(snapshot(2, [...ids, fresh], [fresh])); await f.editor.refresh(); f.editor.rebase();
    expect(f.editor.draft).toEqual([ids[2], fresh]); expect(f.editor.removed).toEqual([old]); expect(f.editor.added).toEqual([fresh]);
    expect(f.editor.removedLabels).toEqual(["Old activity"]);
    expect(f.write).not.toHaveBeenCalled(); f.editor.dispose();
  });
  it("resolves a lost response by reading the committed result without repeating PUT", async () => {
    const f = fixture(); await f.editor.refresh(); f.editor.begin(); f.editor.select([ids[2]]);
    f.write.mockImplementationOnce(async () => { f.set(snapshot(2, [ids[2]])); throw new TypeError("network"); });
    await f.editor.save(); expect(f.write).toHaveBeenCalledTimes(1); expect(f.editor.editing).toBe(false);
    expect(f.editor.visibleIds).toEqual([ids[2]]); f.editor.dispose();
  });
  it("checks hidden record order as well as visible choices when a save is uncertain", async () => {
    const f = fixture(snapshot(1, [ids[0]])); await f.editor.refresh(); f.editor.begin();
    f.write.mockImplementationOnce(async () => {
      const wrong = snapshot(2, [ids[0]]); wrong.layout.entries = [wrong.layout.entries[0], wrong.layout.entries[2], wrong.layout.entries[1]];
      f.set(wrong); throw new TypeError("lost");
    });
    await f.editor.save(); expect(f.editor.conflict).toBe(true); expect(f.editor.editing).toBe(true); expect(f.write).toHaveBeenCalledTimes(1); f.editor.dispose();
  });
  it("retains an uncertain draft until an explicit read succeeds", async () => {
    const f = fixture(); await f.editor.refresh(); f.editor.begin(); f.editor.select([ids[2]]);
    f.write.mockRejectedValueOnce(new TypeError("lost")); f.read.mockRejectedValueOnce(new TypeError("offline"));
    await f.editor.save(); expect(f.editor.pending).toBe(true); expect(f.editor.draft).toEqual([ids[2]]);
    await f.editor.save(); expect(f.write).toHaveBeenCalledTimes(1);
    f.set(snapshot(2, [ids[2]])); await f.editor.refresh(); expect(f.editor.pending).toBe(false); expect(f.editor.editing).toBe(false); f.editor.dispose();
  });
  it("ignores old notifications and refreshes the baseline after reconnection", async () => {
    const f = fixture(); await f.editor.refresh();
    f.editor.invalidate("layout", { layoutId: "layout", revision: 1 });
    f.editor.invalidate("catalog", { catalogRevision: "a".repeat(64) }); expect(f.read).toHaveBeenCalledTimes(1);
    f.editor.begin(); f.editor.disconnect(); expect(f.editor.canSave).toBe(false);
    f.set({ ...snapshot(), catalogRevision: "b".repeat(64) }, 2); await f.editor.refresh();
    expect(f.editor.conflict).toBe(true); expect(f.editor.canSave).toBe(false); f.editor.rebase(); expect(f.editor.canSave).toBe(true); f.editor.dispose();
  });
});
