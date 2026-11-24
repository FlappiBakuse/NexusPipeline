import assert from 'node:assert/strict';
import { createStore } from '../../apps/web/src/app/store';
import { ControlClient, ControlFailure } from '../../apps/web/src/control/client';
import { extensionList } from '../../apps/web/src/control/extensions';
import { notificationCatalog } from '../../apps/web/src/pages/notification-types';
import { preparePages } from '../../apps/web/src/app/page-preparation';

export async function editorDirectoryCases(): Promise<string[]> {
  let notices = 0, extensions = 0;
  let wait = Promise.withResolvers<void>();
  const client = new ControlClient('http://127.0.0.1', async input => {
    switch (new URL(String(input)).pathname) {
      case '/control/v1/notifications': {
        const revision = ++notices;
        await wait.promise;
        return Response.json({ revision, maximumTasks: 20, tasks: [{ id: 'notice', revision,
          name: 'Current notification', remark: '', kind: 'smtp', isEnabled: true, configuration: { smtpPort: 465 } }] });
      }
      case '/control/v1/extensions': {
        const installedRevision = ++extensions;
        return Response.json({ hasReleaseTrust: false, items: [{ extensionId: 'extension', installedRevision, isInstalled: true,
          desiredEnabled: true, activationId: 'active', unavailableReason: null, capabilities: [] }] });
      }
      default: throw new Error('Unexpected directory request');
    }
  });
  const store = createStore(client);
  const access = { instanceId: 'one', hostVersion: '0.18.0', capabilities: ['notifications.read', 'extensions.read'] };
  store.state.capabilities = access;
  store.state.events = { mutationSequence: 1, extensionGeneration: 'first' };
  const fresh = new AbortController(), retired = new AbortController();
  const notification = (signal = fresh.signal) => store.readEditorDirectory('notifications', notificationCatalog, signal);
  const extension = () => store.readEditorDirectory('extensions', extensionList, fresh.signal);
  try {
    const first = notification(), redundant = notification(retired.signal);
    assert.equal(notices, 1);
    retired.abort();
    await assert.rejects(redundant, { name: 'AbortError' });
    wait.resolve();
    const returned = await first;
    returned.tasks[0]!.configuration.smtpPort = 1;
    assert.equal((await notification()).tasks[0]!.configuration.smtpPort, 465);
    assert.equal(notices, 1);

    const earlier = await extension();
    assert.equal((await extension()).items[0]?.installedRevision, earlier.items[0]?.installedRevision);
    store.state.events = { ...store.state.events, extensionGeneration: 'later' };
    assert.equal((await extension()).items[0]?.installedRevision, 2);
    assert.equal((await notification()).revision, 1);

    store.state.events = { ...store.state.events, mutationSequence: 2 };
    wait = Promise.withResolvers<void>();
    const allCancelled = new AbortController();
    const abandoned = notification(allCancelled.signal);
    allCancelled.abort();
    await assert.rejects(abandoned, { name: 'AbortError' });
    wait.resolve();
    assert.equal((await notification()).revision, 3);

    client.authenticate('new-fixture-credential');
    assert.equal((await notification()).revision, 4);
    store.state.capabilities = { ...access, instanceId: 'two' };
    assert.equal((await notification()).revision, 5);
    store.state.capabilities = { ...access, capabilities: ['extensions.read'] };
    await assert.rejects(notification(), error => error instanceof ControlFailure && error.status === 403);
    assert.equal(notices, 5);

    store.state.capabilities = access;
    store.state.events = null;
    assert.equal((await notification()).revision, 6);
    assert.equal((await notification()).revision, 7);

    store.state.events = { mutationSequence: 3, extensionGeneration: 'before-prefetch' };
    store.state.page = 'notifications';
    assert.deepEqual(await preparePages(store, fresh.signal, () => {}, true), { failures: [], revoked: 0 });
    assert.equal(notices, 8);
    assert.equal((await notification()).revision, 8);
    assert.equal(notices, 8);

    store.state.events = { ...store.state.events, mutationSequence: 4 };
    await preparePages(store, fresh.signal, () => {}, true);
    assert.equal(notices, 9);
    store.state.events = { ...store.state.events, mutationSequence: 5 };
    assert.equal((await notification()).revision, 10);
    assert.equal(notices, 10);
  } finally { wait.resolve(); store.dispose(); }
  return ['shared notification GET survives one cancellation and preserves mutable caller isolation',
    'editor catalogs refresh on mutation or activation generation and discard abandoned responses',
    'directory cache expires on authentication, instance, access and unversioned scope changes',
    'page preparation reuses an exact directory scope and cannot seed a later mutation'];
}
