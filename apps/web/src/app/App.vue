<script setup lang="ts">
import { computed, onMounted, onBeforeUnmount, ref, watch, markRaw, provide } from 'vue';
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
import { confirmAction } from './confirmation';
registerMessages({
  '与宿主的连接已中断，请重新连接。': 'Connection to the Host was lost. Reconnect to continue.',
  查询界面设置原操作: 'Check original interface change',
  '界面设置结果待确认，请查询原操作。': 'Interface change outcome is unknown. Check the original operation.'
});
const client = new ControlClient();
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
  })
);
provide(registryKey, registry);
const frontend = markRaw(new FrontendLoader(client, store.media, registry, store.report));
watch(() => [store.state.capabilities?.instanceId, store.state.settings?.saved.revision,
  store.state.events?.extensionGeneration], () => {
  if (store.state.capabilities) void frontend.synchronize().catch(store.report);
  else frontend.disconnect();
});
watch(() => store.state.events, () => { frontend.notify(); });
const routes = ref(registry.listRoutes());
watch(locale, () => {
  registry.refreshLocale();
  routes.value = registry.listRoutes();
});
const overviewAvailable = ref(false), wallpaperAvailable = ref(false);
const stopRoutes = registry.subscribe(() => {
  overviewAvailable.value = registry.listSurfaces('overview.cards').some(item => item.extensionId === 'game-activities');
  wallpaperAvailable.value = registry.listSurfaces('settings.panels').some(item => item.extensionId === 'custom-wallpaper');
  routes.value = registry.listRoutes();
});
const visited = ref(new Set<AppRoute>());
watch(
  () => store.state.capabilities?.instanceId,
  () => {
    visited.value = new Set([store.state.page]);
  }
);
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
  () => store.state.page,
  (page) => {
    visited.value = new Set([...visited.value, page]);
  },
  { immediate: true }
);
async function authenticate(): Promise<void> {
  authenticating.value = true;
  client.authenticate(credential.value);
  credential.value = '';
  try {
    await store.start();
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
  if (client.pendingWrites || store.dirty.size) {
    event.preventDefault();
    event.returnValue = '';
  }
}
onMounted(async () => {
  window.addEventListener('hashchange', locationChanged);
  window.addEventListener('beforeunload', leaving);
  desktop?.prepareExit(
    async (reason) =>
      (reason === 'maintenance' ? client.canExitMaintenance() : client.pendingWrites === 0) &&
      (!store.dirty.size || (await confirmAction(t('存在未保存的草稿，仍要退出窗口？'))))
  );
  try {
    if (desktop) {
      const session = await desktop.connect();
      desktopReady.value = true;
      if (session.route !== '/' && !location.hash) history.replaceState(null, '', '#' + session.route);
    }
    await store.start();
  } catch (error) {
    store.report(error);
  }
});
onBeforeUnmount(() => {
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
  <AmbientLayer :visible="store.state.visible" />
  <ExtensionOutlet
    v-if="store.state.capabilities"
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
            <p class="eyebrow">{{ t(pageMeta[store.state.page]?.eyebrow || '插件') }}</p>
            <h1>{{ title ? t(title[1]) : routeFor(store.state.page)?.label }}</h1>
            <p class="page-description">
              <span class="default-description">{{ t(pageMeta[store.state.page]?.description || '') }}</span>
              <span id="page-description-override"></span>
            </p>
          </div>
          <div class="header-actions"><div id="page-actions"></div></div>
        </header>
        <div v-if="store.state.error" class="error-banner" role="alert">
          <strong>{{ t('操作未完成') }}</strong>
          <span>{{ t(store.state.error === 'connection_lost' ? '与宿主的连接已中断，请重新连接。' : store.state.error) }}</span>
          <button v-if="store.state.error !== 'settings_invalid'"
            @click="
              store.state.error = '';
              store.start().catch(store.report);
            "
          >
            {{ t('重新连接') }}
          </button>
          <p v-if="store.state.uncertainOperation">
            {{ t('写入结果待确认，请先核实操作结果。') }}
            <code>{{ store.state.uncertainOperation }}</code>
            <button
              v-if="store.interfaceChange.pending"
              :disabled="store.interfaceChange.pending.state !== 'unknown'"
              @click="store.inspectInterface().catch(store.report)"
            >
              {{ t('查询界面设置原操作') }}
            </button>
          </p>
        </div>
        <div v-if="store.state.feedError" class="error-banner" role="alert">
          <strong>{{ t('事件连接中断，正在重连') }}</strong>
          <span>{{ t(store.state.feedError === 'connection_lost' ? '与宿主的连接已中断，请重新连接。' : store.state.feedError) }}</span>
        </div>
        <form v-if="!store.state.capabilities" class="authentication" @submit.prevent="authenticate">
          <label>
            {{ t('远程访问令牌') }}
            <input v-model="credential" type="password" autocomplete="off" />
          </label>
          <button :disabled="authenticating">{{ t('连接') }}</button>
        </form>
        <p v-if="store.state.notice" role="status" class="notice-banner">
          {{ t(store.state.notice) }}
          <button :aria-label="t('关闭提示')" @click="store.state.notice = ''">×</button>
        </p>
        <template v-if="store.state.capabilities">
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
              :overview-available="overviewAvailable"
              :wallpaper-available="wallpaperAvailable"
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
