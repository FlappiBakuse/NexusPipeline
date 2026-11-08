import { computed, onBeforeUnmount, reactive, ref, watch } from 'vue';
import type { AppStore } from '../app/store';
import { locale } from '../app/locale';
import { metadataText, type ExtensionMetadata } from '../control/extension-metadata';
import { pluginIndex, matchesPlugin, comparePlugins, type PluginIndex } from './plugin-list';
import {
  extensionCatalog,
  extensionDetails,
  extensionList,
  releaseSummary,
  upgradeAvailable,
  type CatalogEntry,
  type ExtensionCatalog,
  type ExtensionDetails,
  type ExtensionState
} from '../control/extensions';

export interface PluginRow {
  extensionId: string;
  name: string;
  summary: string;
  kind: string;
  version: string;
  installed?: ExtensionState;
  installation?: ExtensionState;
  candidate?: CatalogEntry;
  details?: ExtensionDetails;
  update: boolean;
  metadata?: ExtensionMetadata;
  artifactName?: string;
}

export function usePluginBrowser(store: AppStore, visible: () => boolean) {
  const installed = ref<ExtensionState[]>([]),
    catalog = ref<ExtensionCatalog | null>(null);
  const localDetails = reactive(new Map<string, ExtensionDetails>());
  const catalogDetails = reactive(new Map<string, ExtensionDetails>());
  const view = ref<'installed' | 'store'>('installed'),
    selectedId = ref('');
  const search = ref(''),
    kind = ref('all'),
    order = ref('name'),
    descending = ref(false);
  const states = { installed: { search: '', kind: 'all', order: 'name', descending: false },
    store: { search: '', kind: 'all', order: 'updated', descending: true } };
  let indexes = new Map<string, { stamp: string; value: PluginIndex }>();
  const busy = ref(false),
    detailLoading = ref(false),
    error = ref(''),
    detailError = ref('');
  const instance = store.state.capabilities!.instanceId;
  const current = () => store.state.capabilities?.instanceId === instance;
  const channel = computed(() => store.state.settings?.saved.values.updates.pluginSource || 'stable');
  let owner: AbortController | undefined, detailOwner: AbortController | undefined;
  let loaded = false;
  const rows = computed<PluginRow[]>(() => {
    const ids =
      view.value === 'installed'
        ? installed.value.filter((item) => item.isInstalled).map((item) => item.extensionId)
        : catalog.value?.entries.map((item) => item.extensionId) || [];
    return ids.map((extensionId) => {
      const installation = installed.value.find((item) => item.extensionId === extensionId);
      const state = installation?.isInstalled ? installation : undefined;
      const candidate = catalog.value?.entries.find((item) => item.extensionId === extensionId);
      const details =
        view.value === 'store'
          ? catalogDetails.get(extensionId) || localDetails.get(extensionId)
          : localDetails.get(extensionId);
      const metadata = view.value === 'store' ? candidate?.metadata : state?.metadata;
      const text = metadata && metadataText(metadata, locale.value);
      return {
        extensionId,
        name: text?.displayName || details?.manifest.displayName || state?.artifactName || candidate?.artifactName || extensionId,
        summary: text?.summary || releaseSummary(details, locale.value),
        kind: state?.kind || details?.manifest.kind || candidate?.kind || '',
        version: view.value === 'installed' ? state?.packageVersion || details?.packageVersion || '' : candidate?.packageVersion || '',
        metadata, artifactName: state?.artifactName || candidate?.artifactName,
        installed: state,
        installation,
        candidate,
        details,
        update: !!state && upgradeAvailable(localDetails.get(extensionId), candidate, channel.value)
      };
    });
  });
  const searchIndexes = computed(() => {
    const next = new Map<string, { stamp: string; value: PluginIndex }>();
    for (const row of rows.value) {
      const stamp = JSON.stringify([row.metadata || [row.name, row.summary], row.artifactName, row.extensionId]);
      const prior = indexes.get(row.extensionId);
      next.set(row.extensionId, prior?.stamp === stamp ? prior : { stamp, value: pluginIndex(row) });
    }
    indexes = next; return next;
  });
  const filtered = computed(() => {
    return rows.value
      .filter(row => (kind.value === 'all' || row.kind === kind.value) && matchesPlugin(searchIndexes.value.get(row.extensionId)!.value, search.value))
      .sort((left, right) => comparePlugins(left, right, order.value, descending.value));
  });
  const selected = computed(() => rows.value.find((item) => item.extensionId === selectedId.value));
  const details = computed(() =>
    view.value === 'store' ? catalogDetails.get(selectedId.value) : localDetails.get(selectedId.value)
  );
  async function readLocal(state: ExtensionState, signal: AbortSignal): Promise<void> {
    const cached = localDetails.get(state.extensionId);
    if (cached?.installedRevision === state.installedRevision) return;
    const value = await store.client.read(
      '/control/v1/extensions/' + encodeURIComponent(state.extensionId) + '/details',
      (item) => extensionDetails(item, state.extensionId),
      signal
    );
    signal.throwIfAborted();
    if (value.installedRevision !== state.installedRevision)
      throw new TypeError('extension_details_revision_changed');
    if (current()) localDetails.set(state.extensionId, value);
  }
  async function select(extensionId: string): Promise<void> {
    selectedId.value = extensionId;
    detailOwner?.abort();
    detailError.value = '';
    if (!visible() || !current()) return;
    const controller = new AbortController();
    detailOwner = controller;
    detailLoading.value = true;
    try {
      if (view.value === 'installed') {
        const state = installed.value.find((item) => item.extensionId === extensionId);
        if (state) await readLocal(state, controller.signal);
      } else {
        const cached = catalog.value;
        if (!cached || catalogDetails.has(extensionId)) return;
        const identity = cached.identity;
        const query = new URLSearchParams({
          generationId: identity.generationId,
          sequence: String(identity.sequence),
          sha256: identity.sha256
        });
        const value = await store.client.read(
          '/control/v1/extensions/catalog/' +
            channel.value +
            '/entries/' +
            encodeURIComponent(extensionId) +
            '/details?' +
            query,
          (item) => extensionDetails(item, extensionId),
          controller.signal
        );
        controller.signal.throwIfAborted();
        if (current() && catalog.value === cached) catalogDetails.set(extensionId, value);
      }
    } catch (reason) {
      if (!controller.signal.aborted && current())
        detailError.value = reason instanceof Error ? reason.message : '发行文档暂不可用';
    } finally {
      if (detailOwner === controller) detailLoading.value = false;
    }
  }
  async function load(refresh = false): Promise<void> {
    if (!visible() || !current() || (refresh && !store.can('extensions.write'))) return;
    owner?.abort();
    const controller = new AbortController();
    owner = controller;
    busy.value = true;
    error.value = '';
    try {
      const list = await store.client.read('/control/v1/extensions', extensionList, controller.signal);
      controller.signal.throwIfAborted();
      installed.value = list.items;
      const live = new Set(list.items.filter((item) => item.isInstalled).map((item) => item.extensionId));
      for (const id of localDetails.keys()) if (!live.has(id)) localDetails.delete(id);
      let next: ExtensionCatalog | null = null;
      if (list.hasReleaseTrust)
        next = refresh
          ? await store.client.write(
              '/control/v1/extensions/catalog/' + channel.value + '/refresh',
              null,
              extensionCatalog
            )
          : await store.client.read(
              '/control/v1/extensions/catalog/' + channel.value,
              extensionCatalog,
              controller.signal
            );
      controller.signal.throwIfAborted();
      if (!current()) return;
      if (catalog.value?.identity.sha256 !== next?.identity.sha256) catalogDetails.clear();
      catalog.value = next;
      loaded = true;
      if (!rows.value.some((item) => item.extensionId === selectedId.value))
        selectedId.value = filtered.value[0]?.extensionId || '';
      if (selectedId.value) await select(selectedId.value);
    } catch (reason) {
      if (!controller.signal.aborted && current()) {
        store.report(reason);
        error.value = reason instanceof Error ? reason.message : '插件读取失败';
      }
    } finally {
      if (owner === controller) busy.value = false;
    }
  }
  watch(view, (next, previous) => {
    Object.assign(states[previous], { search: search.value, kind: kind.value, order: order.value, descending: descending.value });
    const saved = states[next];
    search.value = saved.search; kind.value = saved.kind; order.value = saved.order; descending.value = saved.descending;
  });
  watch([view, kind, order, descending, search], () => {
    if (!filtered.value.some((item) => item.extensionId === selectedId.value))
      selectedId.value = filtered.value[0]?.extensionId || '';
    if (selectedId.value) void select(selectedId.value);
  });
  watch(channel, () => {
    catalog.value = null;
    catalogDetails.clear();
    if (visible()) void load();
  });
  watch(
    visible,
    (active) => {
      if (active) {
        if (!loaded) void load();
        else if (selectedId.value) void select(selectedId.value);
      } else {
        owner?.abort();
        detailOwner?.abort();
      }
    },
    { immediate: true }
  );
  onBeforeUnmount(() => {
    owner?.abort();
    detailOwner?.abort();
  });
  watch(() => store.state.preparationRevision, () => {
    if (visible()) void load();
  });
  return {
    installed,
    catalog,
    view,
    selectedId,
    selected,
    rows,
    filtered,
    search,
    kind,
    order,
    descending,
    busy,
    detailLoading,
    error,
    detailError,
    details,
    channel,
    select,
    load
  };
}

export function compatiblePlugin(row: PluginRow, hostVersion: string): boolean {
  const minimum = row.candidate?.minHostVersion;
  if (!minimum) return true;
  const current = hostVersion.split('.').map(Number);
  const required = minimum.split('.').map(Number);
  for (let index = 0; index < 3; index++) {
    if (!Number.isFinite(current[index]) || !Number.isFinite(required[index])) return false;
    if (current[index] !== required[index]) return current[index]! > required[index]!;
  }
  return true;
}
