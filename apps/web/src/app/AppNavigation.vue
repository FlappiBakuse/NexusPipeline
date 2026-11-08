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
<style src="./app-navigation.css"></style>
