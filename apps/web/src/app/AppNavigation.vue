<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { navigation, type AppRoute, type AppStore } from './store';
import type { FrontendRegistry } from '../extensions/frontend-registry';
import { t } from './locale';
const props = defineProps<{
  store: AppStore;
  routes: ReturnType<FrontendRegistry['listRoutes']>;
  desktopMode: boolean;
  themeBusy: boolean;
  themeIcon: string;
}>();
const emit = defineEmits<{ theme: [] }>();
const store = props.store;
const drawer = ref(false);
const menuButton = ref<HTMLButtonElement>();
const sidebar = ref<HTMLElement>();
const compactLayout = matchMedia('(max-width: 820px)');
const compact = ref(compactLayout.matches);
watch(drawer, async (open, previous) => {
  if (open) {
    await nextTick();
    if (drawer.value) sidebar.value?.querySelector<HTMLElement>('[aria-current=page]')?.focus();
  } else if (previous && compactLayout.matches) {
    await nextTick();
    if (!drawer.value) menuButton.value?.focus();
  }
});
function layoutChanged(): void {
  const active = document.activeElement;
  compact.value = compactLayout.matches;
  if (!compact.value) {
    drawer.value = false;
    if (active === menuButton.value)
      void nextTick(() => sidebar.value?.querySelector<HTMLElement>('[aria-current=page]')?.focus());
  } else if (!drawer.value && active && sidebar.value?.contains(active))
    void nextTick(() => menuButton.value?.focus());
}
function visit(event: MouseEvent, page: AppRoute): void {
  if (event.button || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
  event.preventDefault();
  drawer.value = false;
  void store.navigate(page);
}
function escapeDrawer(event: KeyboardEvent): void {
  if (drawer.value && event.key === 'Escape') {
    event.preventDefault();
    drawer.value = false;
  }
}
const serviceAddress = computed(() => {
  const port = store.state.status?.currentWebPort;
  return t('服务') + (port ? ' · ' + location.hostname + ':' + port : '');
});
const groups = [
  { label: '工作空间', pages: navigation.slice(0, 3) },
  { label: '管理', pages: navigation.slice(3, 6) },
  { label: '扩展与系统', pages: navigation.slice(6) }
];

watch(
  () => store.state.page,
  () => {
    drawer.value = false;
  }
);
onMounted(() => {
  compactLayout.addEventListener('change', layoutChanged);
  window.addEventListener('keydown', escapeDrawer);
});
onBeforeUnmount(() => {
  compactLayout.removeEventListener('change', layoutChanged);
  window.removeEventListener('keydown', escapeDrawer);
});
</script>
<template>
  <div class="app-shell" :class="{ 'desktop-shell': desktopMode }">
    <aside
      id="main-navigation"
      ref="sidebar"
      class="sidebar"
      :class="{ expanded: drawer }"
      :inert="compact && !drawer"
      :aria-hidden="compact ? !drawer : undefined"
    >
      <div class="brand">
        <span class="brand-mark" aria-hidden="true">N</span>
        <span>
          NexusPipeline
          <small>{{ t('枢链') }}</small>
        </span>
      </div>
      <nexus-scroll-area class="sidebar-navigation" :label="t('主导航')">
        <nav :aria-label="t('主导航')">
          <section v-for="group in groups" :key="group.label" class="nav-group">
            <p>{{ t(group.label) }}</p>
            <template v-if="group.label === '扩展与系统'">
              <a
                v-for="route in routes"
                :key="route.path"
                :href="'#' + route.path"
                :aria-current="'/' + store.state.page === route.path ? 'page' : undefined"
                @click="visit($event, route.path.slice(1) as AppRoute)"
              >
                <span class="nav-icon"><nexus-icon aria-hidden="true" :name="route.path.startsWith('/plugin/game-checkin') ? 'checkin' : 'plugins'" /></span>
                {{ route.label }}
              </a>
            </template>
            <a
              v-for="item in group.pages"
              :key="item[0]"
              :href="'#/' + item[0]"
              :aria-current="store.state.page === item[0] ? 'page' : undefined"
              @click="visit($event, item[0])"
            >
              <span class="nav-icon">
                <nexus-icon aria-hidden="true" :name="item[0] === 'notifications' ? 'bell' : item[0]" />
              </span>
              <span>{{ t(item[1]) }}</span>
            </a>
          </section>
        </nav>
      </nexus-scroll-area>
      <div class="sidebar-footer">
        <div>
          <p
            role="status"
            :aria-label="
              store.state.feedError
                ? t('事件连接中断，正在重连')
                : store.state.status
                  ? t('服务已连接')
                  : t('等待连接')
            "
          >
            {{ serviceAddress }}
          </p>
          <small>{{ t('当前版本') }} · {{ store.state.capabilities?.hostVersion || '0.18.0' }}</small>
        </div>
        <button
          class="theme-toggle"
          :disabled="themeBusy || !!store.interfaceChange.pending || !store.can('settings.write')"
          :aria-label="t('切换主题')"
          @click="emit('theme')"
        >
          <nexus-icon aria-hidden="true" :name="themeIcon" />
        </button>
      </div>
    </aside>
    <button
      class="drawer-scrim"
      :class="{ active: drawer }"
      :inert="!drawer"
      :aria-hidden="!drawer"
      :tabindex="drawer ? 0 : -1"
      :aria-label="t('关闭导航')"
      @click="drawer = false"
    />
    <div class="page-shell" :inert="drawer">
      <header class="mobile-topbar">
        <button
          ref="menuButton"
          class="menu-toggle"
          :aria-expanded="drawer"
          aria-controls="main-navigation"
          :aria-label="t('打开导航')"
          @click="drawer = true"
        >
          <nexus-icon aria-hidden="true" name="menu" />
        </button>
        <span class="topbar-product">{{ t('NexusPipeline 枢链') }}</span>
        <button
          class="theme-toggle"
          :disabled="themeBusy || !!store.interfaceChange.pending || !store.can('settings.write')"
          :aria-label="t('切换主题')"
          @click="emit('theme')"
        >
          <nexus-icon aria-hidden="true" :name="themeIcon" />
        </button>
      </header>
      <slot />
    </div>
  </div>
</template>
<style>
.app-shell {
  position: relative;
  z-index: 2;
  display: grid;
  grid-template-columns: 222px minmax(0, 1fr);
  height: 100dvh;
  overflow: hidden;
}
.app-shell.desktop-shell {
  height: calc(100dvh - 40px);
}
.sidebar {
  min-height: 0;
  padding: 18px 12px 14px;
  background: var(--panel);
  border-right: 1px solid var(--border);
  display: flex;
  flex-direction: column;
}
.brand {
  display: flex;
  flex: none;
  gap: 11px;
  min-height: 44px;
  align-items: center;
  font-size: 14px;
  font-weight: 700;
  padding: 0 8px;
}
.brand-mark {
  display: grid;
  place-items: center;
  width: 34px;
  height: 34px;
  color: white;
  font-weight: 800;
  border-radius: 10px;
  background: linear-gradient(135deg, var(--accent), #7e6cff);
}
.brand small {
  display: block;
  margin-top: 2px;
  color: var(--muted);
  font-size: 11px;
  font-weight: 400;
}
.sidebar-navigation {
  flex: 1;
  min-height: 0;
  min-width: 0;
}
.sidebar nav {
  display: flow-root;
  min-height: 0;
  padding-inline: 2px;
}
.nav-group {
  margin: 0;
}
.nav-group > p {
  margin: 24px 10px 8px;
  font-size: 11px;
  font-weight: 700;
  letter-spacing: 0.08em;
  color: var(--faint);
}
.nav-group > a {
  display: flex;
  gap: 11px;
  align-items: center;
  width: 100%;
  min-height: 42px;
  border: 1px solid transparent;
  border-radius: 9px;
  background: transparent;
  padding: 0 12px;
  color: var(--muted);
  font-size: 13px;
  font-weight: 500;
  text-align: left;
  text-decoration: none;
  transition:
    background 150ms,
    color 150ms;
}
.nav-group > a + a {
  margin-top: 5px;
}
.nav-group > a:hover {
  background: var(--hover-bg);
  color: var(--text);
}
.nav-group > a[aria-current='page'] {
  color: var(--accent);
  background: var(--accent-soft);
  font-weight: 650;
}
.nav-icon {
  display: inline-grid;
  place-items: center;
  width: 20px;
  height: 20px;
  flex: none;
}
.nav-icon nexus-icon {
  width: 18px;
  height: 18px;
}
.sidebar-footer {
  flex: none;
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 14px 4px 0 10px;
  border-top: 1px solid var(--border);
  color: var(--faint);
  font-size: 11px;
}
.sidebar-footer > div {
  min-width: 0;
  display: grid;
  gap: 3px;
}
.sidebar-footer p,
.sidebar-footer small {
  overflow: hidden;
  margin: 0;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 11px;
}
.theme-toggle {
  flex: none;
  display: inline-grid;
  place-items: center;
  min-width: 40px;
  width: 40px;
  min-height: 40px;
  padding: 8px;
  border-color: transparent;
  background: transparent;
}
.theme-toggle nexus-icon,
.menu-toggle nexus-icon {
  width: 18px;
  height: 18px;
}
.mobile-topbar,
.drawer-scrim {
  display: none;
}
@media (max-width: 820px) {
  .app-shell {
    grid-template-columns: minmax(0, 1fr);
  }
  .sidebar {
    position: fixed;
    inset: 0 auto 0 0;
    z-index: 60;
    width: 222px;
    transform: translateX(-102%);
    transition: transform 250ms ease;
  }
  .sidebar.expanded {
    transform: translateX(0);
  }
  .drawer-scrim {
    display: block;
    position: fixed;
    inset: 0;
    z-index: 50;
    padding: 0;
    border: 0;
    border-radius: 0;
    background: var(--mask);
    opacity: 0;
    pointer-events: none;
    transition: opacity 250ms ease;
  }
  .drawer-scrim.active {
    opacity: 1;
    pointer-events: auto;
  }
  .mobile-topbar {
    display: flex;
    flex: none;
    align-items: center;
    gap: 12px;
    min-height: 56px;
    padding: 8px 16px;
    border-bottom: 1px solid var(--border);
    background: var(--panel);
  }
  .topbar-product {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    font-size: 14px;
    font-weight: 700;
    letter-spacing: 0.01em;
  }
  .mobile-topbar > button {
    min-width: 40px;
    width: 40px;
    min-height: 40px;
    padding: 8px;
    display: inline-grid;
    place-items: center;
  }
  .mobile-topbar > .theme-toggle {
    border-color: var(--border-strong);
    background: var(--content-control);
  }
}
@media (max-width: 600px) {
  .mobile-topbar {
    min-height: 60px;
  }
  .mobile-topbar > button {
    width: 44px;
    min-width: 44px;
    min-height: 44px;
  }
}
</style>
