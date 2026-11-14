<script setup lang="ts">
import { computed, nextTick, onMounted, onBeforeUnmount, ref, watch, markRaw, provide } from 'vue';
import { ControlClient } from '../control/client';
import { createStore, navigation, routePage, type PageId, type AppRoute } from './store';
import { desktop } from './desktop';
import AmbientLayer from './AmbientLayer.vue';
import AppNavigation from './AppNavigation.vue';
import { useAppAppearance } from './app-appearance';
import DesktopTitlebar from './DesktopTitlebar.vue';
import DashboardPage from '../pages/DashboardPage.vue';
import DispatchPage from '../pages/DispatchPage.vue';
import AutomationPage from '../pages/AutomationPage.vue';
import HistoryPage from '../pages/HistoryPage.vue';
import PluginsPage from '../pages/PluginsPage.vue';
import NotificationsPage from '../pages/NotificationsPage.vue';
import SettingsPage from '../pages/SettingsPage.vue';
import { FrontendRegistry } from '../extensions/frontend-registry';
import SurfaceOutlet from '../extensions/SurfaceOutlet.vue';
import ExtensionOutlet from '../extensions/ExtensionOutlet.vue';
import { registryKey } from '../extensions/registry-context';
import { FrontendLoader } from '../extensions/frontend-loader';
import { locale, t, registerMessages } from './locale';
import { pageMeta } from './page-meta';
import { prepareAppExit } from './exit-preparation';
import StartupPreparation from './StartupPreparation.vue';
import { interfacePreparation } from './interface-preparation';
import { clearExtensionIcons } from '../control/extension-icons';
import { useDesktopExit } from './desktop-exit-state';
registerMessages({
  '部分界面准备失败，可重新准备。': 'Some interface preparation failed. Retry preparation.',
  '与宿主的连接已中断，请重新连接。': 'Connection to the Host was lost. Reconnect to continue.',
  查询界面设置原操作: 'Check original interface change',
  '界面设置结果待确认，请查询原操作。': 'Interface change outcome is unknown. Check the original operation.'
});
const client = new ControlClient();
const { exitMessage, exitFailed } = useDesktopExit(client);
const store = createStore(client);
const { theme, themeBusy, themeNotice, themeIcon, toggleTheme } = useAppAppearance(store);
const registry = markRaw(
  new FrontendRegistry({
    get locale() {
      return locale.value;
    },
    get appearance() {
      return theme.value;
    },
    translate: (_id, key) => t(key)
  }, store.editors)
);
provide(registryKey, registry);
const frontend = markRaw(new FrontendLoader(client, store.media, registry, error => preparation.report(error)));
watch(() => [store.state.capabilities?.instanceId, store.state.capabilities?.capabilities.join(','),
  store.state.events?.extensionGeneration, !!store.state.feedError], (next, previous) => {
  if (!previous || previous[0] === undefined || previous[2] === undefined) return;
  if (next.every((value, index) => value === previous[index])) return;
  if (!store.state.capabilities) { preparation.cancel(); frontend.disconnect(); clearExtensionIcons(); preparation.preparing.value = false; return; }
  if (next[3]) return;
  if (next[0] !== previous[0] || next[1] !== previous[1]) {
    preparation.cancel(); client.clearPrepared(); frontend.disconnect(); clearExtensionIcons();
  }
  void prepareInterface().catch(store.report);
});
watch(() => store.state.events, () => { frontend.notify(); });
const routes = ref(registry.listRoutes());
watch(locale, () => {
  registry.refreshLocale();
  routes.value = registry.listRoutes();
});
const overviewCards = ref(registry.listSurfaces('overview.cards'));
const stopRoutes = registry.subscribe(() => {
  overviewCards.value = registry.listSurfaces('overview.cards');
  routes.value = registry.listRoutes();
});
const visited = ref(new Set<AppRoute>());
watch(
  () => store.state.capabilities?.instanceId,
  () => {
    visited.value = new Set();
  }
);
const preparation = interfacePreparation(store, frontend, registry);
const { preparing, surfacesReady, failed: preparationFailed, coreFailed, stage: preparationStage, count: preparationCount } = preparation;
const prepareInterface = preparation.prepare;
const credential = ref('');
const desktopReady = ref(false);
const authenticating = ref(false);
const title = computed(() => navigation.find((item) => item[0] === store.state.page));
const routeFor = (page: AppRoute) => routes.value.find((route) => route.path === '/' + page);
const isHost = (page: AppRoute): page is PageId => navigation.some((item) => item[0] === page);
const pages = markRaw({
  dashboard: DashboardPage,
  dispatch: DispatchPage,
  scripts: AutomationPage,
  users: AutomationPage,
  queues: AutomationPage,
  history: HistoryPage,
  plugins: PluginsPage,
  notifications: NotificationsPage,
  settings: SettingsPage
});
const context = (target: 'overview.cards' | 'settings.panels' | 'settings.extra') => ({
  surfaceId: target,
  instanceId: store.state.capabilities?.instanceId || ''
});
watch(
  () => [store.state.page, surfacesReady.value] as const,
  ([page, ready]) => {
    if (ready) visited.value = new Set([...visited.value, page]);
  },
  { immediate: true }
);
async function authenticate(): Promise<void> {
  authenticating.value = true;
  frontend.disconnect();
  clearExtensionIcons();
  if (credential.value) client.authenticate(credential.value);
  credential.value = '';
  try {
    await prepareInterface();
  } catch (error) {
    store.report(error);
  } finally {
    authenticating.value = false;
  }
}
function locationChanged(): void {
  void store.navigate(routePage(), true);
}
function leaving(event: BeforeUnloadEvent): void {
  if (client.pendingWrites) {
    event.preventDefault();
    event.returnValue = '';
  }
}
onMounted(async () => {
  window.addEventListener('hashchange', locationChanged);
  window.addEventListener('beforeunload', leaving);
  desktop?.prepareExit(prepareAppExit(client));
  try {
    if (desktop) {
      const session = await desktop.connect();
      desktopReady.value = true;
      if (session.route !== '/' && !location.hash) history.replaceState(null, '', '#' + session.route);
    }
    await prepareInterface();
  } catch (error) {
    store.report(error);
    preparing.value = false;
    await nextTick();
    await desktop?.presented?.();
  }
});
onBeforeUnmount(() => {
  preparation.cancel();
  clearExtensionIcons();
  client.clearPrepared();
  void frontend.dispose().catch(store.report);
  stopRoutes();
  store.dispose();
  window.removeEventListener('hashchange', locationChanged);
  window.removeEventListener('beforeunload', leaving);
});
</script>
<template>
  <DesktopTitlebar
    v-if="desktop"
    :ready="desktopReady"
    :disconnected="!!store.state.feedError"
    @error="store.report"
  />
  <StartupPreparation v-if="preparing" :stage="preparationStage" :completed="preparationCount.done" :total="preparationCount.total" />
  <AmbientLayer :visible="store.state.visible" />
  <ExtensionOutlet
    v-if="store.state.capabilities && surfacesReady"
    class="appearance-layer"
    :store="store"
    target="application.appearance"
    :visible="store.state.visible"
  />
  <AppNavigation
    :store="store"
    :routes="routes"
    :desktop-mode="!!desktop"
    :theme-busy="themeBusy"
    :theme-icon="themeIcon"
    @theme="toggleTheme"
  >
    <div v-if="store.state.busy" class="navigation-progress" role="progressbar" :aria-label="t('准备页面')">
      <span />
    </div>
    <nexus-scroll-area class="workspace-scroll" :label="t('主内容')">
      <main class="workspace">
        <header v-show="!!title" class="workspace-header">
          <div class="page-heading">
            <h1>{{ title ? t(title[1]) : routeFor(store.state.page)?.label }}</h1>
            <p class="page-description">
              <span class="default-description">{{ t(pageMeta[store.state.page]?.description || '') }}</span>
              <span id="page-description-override"></span>
            </p>
          </div>
          <div class="header-actions"><div id="page-actions"></div>
            <nexus-button v-if="preparationFailed && !coreFailed" :disabled.prop="preparing" @click="prepareInterface().catch(store.report)">{{ t('重试') }}</nexus-button>
            <nexus-button v-if="store.state.error === 'connection_lost'" @click="authenticate">{{ t('重新连接') }}</nexus-button>
          </div>
        </header>
        <nexus-toast v-if="store.state.error || store.state.uncertainOperation" class="operation-toast" tone="danger"
          :message.prop="t(store.state.uncertainOperation ? '写入结果待确认，请返回原操作查询状态。' : store.state.error === 'connection_lost' ? '与宿主的连接已中断，请重新连接。' : store.state.error)">
        </nexus-toast>
        <nexus-toast v-if="store.state.feedError" class="connection-toast" tone="warning" :message.prop="t('事件连接中断，正在重连')" />
        <nexus-toast v-if="exitMessage" :tone="exitFailed ? 'warning' : 'info'" :message.prop="t(exitMessage)" />
        <form v-if="!preparing && (!store.state.capabilities || coreFailed)" class="authentication" @submit.prevent="authenticate">
          <label>
            {{ t('远程访问令牌') }}
            <input v-model="credential" type="password" autocomplete="off" />
          </label>
          <button :disabled="authenticating">{{ t('连接') }}</button>
        </form>
        <nexus-toast v-if="store.state.notice" :message.prop="t(store.state.notice)" @nexus-close="store.state.notice = ''" />
        <nexus-toast v-if="preparationFailed && !coreFailed" tone="warning" :message.prop="t('部分界面准备失败，可重新准备。')" />
        <template v-if="store.state.capabilities && surfacesReady">
          <section
            v-for="page in visited"
            :key="(store.state.capabilities?.instanceId || '') + '/' + page"
            v-show="store.state.page === page"
            class="app-page"
            :aria-label="
              t(navigation.find((item) => item[0] === page)?.[1] || routeFor(page)?.label || '扩展页面不可用')
            "
          >
            <component
              v-if="isHost(page)"
              :is="pages[page]"
              :overview-cards="overviewCards"
              :store="store"
              :kind="page"
              :visible="store.state.visible && store.state.page === page"
            >
              <template #settings-panels>
                <SurfaceOutlet
                  :registry="registry"
                  target="settings.panels"
                  :context="context('settings.panels')"
                  :visible="store.state.visible && page === store.state.page"
                  @error="store.report"
                />
              </template>
              <template #settings-extra>
                <SurfaceOutlet
                  :registry="registry"
                  target="settings.extra"
                  :context="context('settings.extra')"
                  :visible="store.state.visible && page === store.state.page"
                  @error="store.report"
                />
              </template>
            </component>
            <ExtensionOutlet
              v-else-if="routeFor(page)"
              :store="store"
              target="application.navigation"
              :entry="routeFor(page)!"
              :entity-id="routeFor(page)!.id"
              :visible="store.state.visible && store.state.page === page"
            />
            <nexus-empty-state v-else :title="t('扩展页面不可用')" />
          </section>
        </template>
      </main>
    </nexus-scroll-area>
  </AppNavigation>
  <nexus-toast
    v-if="themeNotice"
    class="theme-notice"
    :message.prop="themeNotice"
    tone="neutral"
    @nexus-close="themeNotice = ''"
  />
</template>
