import assert from 'node:assert/strict';
import { ControlClient } from '../../apps/web/src/control/client';
import { createStore } from '../../apps/web/src/app/store';
import { automationSession } from '../../apps/web/src/pages/automation-editor';
import { fixture } from './automation-fixture';
import { sessionCases } from './app-session';
import { snapshotCases } from './session-snapshots';
import { sourceCases } from './automation-source';
import { automationRefreshCases, pluginRefreshCases, settingsRefreshCases } from './automation-refresh';
import { editorDirectoryCases } from './editor-directories';
export async function run(): Promise<string[]> {
  const previous = Object.getOwnPropertyDescriptor(globalThis, 'document');
  Object.defineProperty(globalThis, 'document', { configurable: true, value: Object.assign(new EventTarget(), { hidden: false }) });
  const store = createStore(new ControlClient('http://127.0.0.1'));
  try {
    const first = automationSession(store, 'queues');
    first.notice = 'retained';
    automationSession(store, 'users').notice = 'user notice';
    assert.equal(automationSession(store, 'users').notice, 'user notice');
    assert.equal(automationSession(store, 'queues').notice, 'retained');
    const cases = ['queue owner survives page reattachment and stays separate from users'];
    const reopened = fixture();
    reopened.editor.edit(reopened.queue);
    reopened.editor.editing.value = false;
    reopened.editor.releasePristine();
    reopened.store.state.automation!.catalogRevision = 2;
    const refreshed = { ...reopened.queue, name: 'Updated elsewhere' };
    reopened.store.state.automation!.revisions[0]!.revision = 2;
    reopened.editor.edit(refreshed);
    assert.equal(reopened.editor.draft.value.name, refreshed.name);
    assert.equal(reopened.session.catalogs[reopened.editor.draftKey.value], 2);
    assert.equal(reopened.session.revisions[reopened.editor.draftKey.value], 2);
    reopened.editor.draft.value = { ...refreshed, name: 'Unsaved local edit' };
    reopened.editor.edit(reopened.queue);
    assert.equal(reopened.editor.draft.value.name, 'Unsaved local edit');
    reopened.store.dispose();
    cases.push('reopening an untouched editor refreshes revisions while retaining a modified draft');
    for (const mode of ['saved', 'conflict', 'unknown', 'instance', 'new'] as const) {
      const item = fixture();
      try {
        item.store.drafts.set('unrelated-queue', { keep: true });
        item.editor.edit(mode === 'new' ? undefined : item.queue);
        const key = item.editor.draftKey.value;
        item.editor.draft.value = { ...item.editor.draft.value, name: 'First edit' };
        if (mode === 'new') {
          item.editor.editing.value = false;
          assert(item.editor.resumeNew());
          assert.equal(item.editor.draft.value.name, 'First edit');
        }
        if (mode === 'conflict' || mode === 'unknown' || mode === 'instance') item.fail(mode);
        await item.save.save();
        if (mode === 'saved' || mode === 'new') {
          assert(!item.store.drafts.has(key)); assert(!item.store.drafts.has(key + '/schedules'));
          const saved = item.store.state.automation!.bundle;
          assert.equal(saved.queues[0]!.name, 'First edit');
          item.editor.edit(saved.queues[0]);
          item.editor.draft.value = { ...item.editor.draft.value, name: 'Second edit' };
          await item.save.save();
          assert.equal(item.requests.length, 2);
          const updated = item.store.state.automation!.bundle;
          assert.equal(updated.queues[0]!.name, 'Second edit');
          assert.equal(updated.schedules[0]!.id, saved.schedules[0]!.id);
        } else {
          assert(item.store.drafts.has(key));
          if (mode === 'conflict') {
            assert.equal(item.session.pending, null); item.editor.discard(); assert(!item.store.drafts.has(key));
          } else {
            assert.equal(item.session.pending!.id, item.requests[0].operationId);
            if (mode === 'unknown') {
              await item.save.save(); assert.equal(item.requests.length, 1);
              await item.commands.check(); assert.equal(item.requests.length, 1);
              assert.equal(item.session.pending, null); assert(!item.store.drafts.has(key));
            }
          }
        }
        assert(item.store.drafts.has('unrelated-queue'));
        cases.push('queue real editor save/reopen and owned draft policy: ' + mode);
      } finally { item.store.dispose(); }
    }
    const confirmed = fixture();
    try {
      confirmed.editor.edit(confirmed.queue);
      const draft = confirmed.editor.draftKey.value;
      confirmed.editor.draft.value = { ...confirmed.editor.draft.value, name: 'Already saved' };
      confirmed.store.state.capabilities!.capabilities = ['automation.write'];
      const refresh = confirmed.store.refreshCatalog;
      confirmed.store.refreshCatalog = async () => { throw new Error('snapshots_unavailable'); };
      confirmed.store.client.read = async () => { throw new Error('confirmed writes need no status read'); };
      await confirmed.save.save();
      assert.equal(confirmed.session.pending?.id, confirmed.requests[0].operationId);
      assert.equal(confirmed.session.pending?.definitionCommitted, true);
      assert.match(confirmed.session.error, /已提交.*刷新/);
      await confirmed.save.save();
      assert.equal(confirmed.requests.length, 1);
      confirmed.store.refreshCatalog = refresh;
      await confirmed.commands.check();
      assert.equal(confirmed.session.pending, null);
      assert(!confirmed.store.drafts.has(draft));
      assert.equal(confirmed.store.state.automation!.bundle.queues[0]!.name, 'Already saved');
      assert.equal(confirmed.requests.length, 1);
      cases.push('refresh failure retains the confirmed operation and retries only the refresh');
    } finally { confirmed.store.dispose(); }
    return [...cases, ...await sessionCases(), ...await snapshotCases(), ...await sourceCases(),
      ...await automationRefreshCases(), ...await settingsRefreshCases(), ...await pluginRefreshCases(), ...await editorDirectoryCases()];
  } finally {
    store.dispose();
    if (previous) Object.defineProperty(globalThis, 'document', previous);
    else Reflect.deleteProperty(globalThis, 'document');
  }
}
