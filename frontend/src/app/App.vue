<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from "vue";
import { useRoute } from "vue-router";
import { useShellStore } from "../stores/shell";
import PluginRouteHost from "./PluginRouteHost";
import BootLoadingState from "./BootLoadingState.vue";
import DashboardPage from "../features/dashboard/DashboardPage.vue";
import HistoryPage from "../features/history/HistoryPage.vue";
import PluginsPage from "../features/plugins/PluginsPage.vue";
import QueuesPage from "../features/queues/QueuesPage.vue";
import DispatchPage from "../features/dispatch/DispatchPage.vue";
import SettingsPage from "../features/settings/SettingsPage.vue";
import ScriptsPage from "../features/scripts/ScriptsPage.vue";
import UsersPage from "../features/users/UsersPage.vue";
import { bootstrapFrontend } from "../legacy/bootstrap";
import { applyTranslations, t } from "@legacy/core/i18n.js";
import { setNavOpen } from "@legacy/core/ui.js";
import { cycleTheme } from "@legacy/core/ui.js";
import { enterPage } from "@legacy/core/state.js";
import { isAbortError } from "@legacy/core/api.js";
import NxpIconButton from "../ui/primitives/NxpIconButton.vue";
import NxpIcon from "../ui/primitives/NxpIcon.vue";
import { installModalBehavior } from "../ui/modal";

const route = useRoute();
const shell = useShellStore();
const isMobile = ref(false);
const segments = computed(() => {
  const path = String(route.path || "/dashboard").replace(/^\/+|\/+$/g, "");
  return (path || "dashboard").split("/").filter(Boolean);
});
const navigation = [
  ["dashboard", "dashboard", "shell.dashboard"],
  ["users", "users", "shell.users"],
  ["scripts", "scripts", "shell.scripts"],
  ["queues", "queues", "shell.queues"],
  ["dispatch", "dispatch", "shell.dispatch"],
  ["history", "history", "shell.history"],
  ["plugins", "plugins", "shell.plugins"],
  ["settings", "settings", "shell.settings"],
] as const;
let uninstallModalBehavior: (() => void) | null = null;

function closeOnBackdrop(event: Event) {
  const target = event.target instanceof Element ? event.target : null;
  if (target?.closest(".nav-backdrop")) shell.navOpen = false;
}

function closeOnDesktopResize() {
  isMobile.value = window.innerWidth <= 820;
  if (window.innerWidth > 820 && shell.navOpen) closeNav();
}

watch(() => route.fullPath, () => {
  enterPage(segments.value[0] || "dashboard");
  if (shell.navOpen) closeNav();
});

onBeforeUnmount(() => {
  uninstallModalBehavior?.();
  uninstallModalBehavior = null;
  document.removeEventListener("click", closeOnBackdrop, true);
  window.removeEventListener("resize", closeOnDesktopResize);
  setNavOpen(false);
});

onMounted(async () => {
  uninstallModalBehavior = installModalBehavior();
  closeOnDesktopResize();
  document.addEventListener("click", closeOnBackdrop, true);
  window.addEventListener("resize", closeOnDesktopResize);
  try {
    const booted = await bootstrapFrontend();
    if (booted) {
      shell.markBooted();
      applyTranslations();
    }
  } catch (error) {
    if (!isAbortError(error)) shell.markBootError(error);
  }
});

function closeNav() {
  shell.closeNav();
  setNavOpen(false);
}

function openNav() {
  shell.navOpen = true;
  setNavOpen(true);
}
</script>

<template>
  <div class="app-shell" :class="{ 'nav-open': shell.navOpen }">
    <aside id="sidebar" class="sidebar" :aria-label="t('shell.nav.main')" data-i18n-aria-label="shell.nav.main" :aria-hidden="isMobile ? (shell.navOpen ? 'false' : 'true') : undefined">
      <div class="brand"><div class="brand-mark" aria-hidden="true">N</div><div class="brand-copy"><strong>NexusPipeline</strong><span data-i18n="shell.brand_suffix"></span></div></div>
      <div class="nav-caption" data-i18n="shell.workbench"></div>
        <nav class="main-nav" :aria-label="t('shell.workbench')" data-i18n-aria-label="shell.workbench">
        <a v-for="[path, icon, label] in navigation.slice(0, 6)" :key="path" :href="`#/${path}`" :data-page="path" :data-testid="`nav-${path}`" :class="{ active: segments[0] === path }" :aria-current="segments[0] === path ? 'page' : undefined" @click="closeNav"><span class="nav-icon"><NxpIcon :name="icon" /></span><span :data-i18n="label"></span></a>
      </nav>
      <div class="nav-caption nav-caption-bottom" data-i18n="shell.system"></div>
      <nav class="main-nav" :aria-label="t('shell.system')" data-i18n-aria-label="shell.system">
        <a v-for="[path, icon, label] in navigation.slice(6)" :key="path" :href="`#/${path}`" :data-page="path" :data-testid="`nav-${path}`" :class="{ active: segments[0] === path }" :aria-current="segments[0] === path ? 'page' : undefined" @click="closeNav"><span class="nav-icon"><NxpIcon :name="icon" /></span><span :data-i18n="label"></span></a>
      </nav>
      <nav class="main-nav plugin-nav-host" data-plugin-anchor="shell.nav" :aria-label="t('shell.nav.plugins')" data-i18n-aria-label="shell.nav.plugins"></nav>
      <div class="sidebar-foot"><div class="sidebar-foot-copy"><span id="local-addr" data-testid="local-addr"></span><span id="app-version" data-i18n="shell.current_version"></span></div><NxpIconButton :label="t('shell.theme_toggle')" data-i18n-aria-label="shell.theme_toggle" @click="cycleTheme"><span data-theme-icon aria-hidden="true"><NxpIcon name="theme" /></span></NxpIconButton></div>
    </aside>
    <div class="nav-backdrop" @click.capture="closeNav"><button type="button" :aria-label="t('shell.close_navigation')" data-i18n-aria-label="shell.close_navigation" @pointerdown="closeNav" @click.stop="closeNav"></button></div>
    <div class="page-shell">
      <header class="topbar"><NxpIconButton class="menu-button" :label="t('shell.open_navigation')" data-i18n-aria-label="shell.open_navigation" :expanded="shell.navOpen" aria-controls="sidebar" @click="openNav"><NxpIcon name="menu" /></NxpIconButton><div class="topbar-context"><span class="topbar-product" data-i18n="shell.product"></span><span id="topbar-title" class="sr-only" data-i18n="shell.dashboard"></span></div><div class="topbar-actions"><NxpIconButton :label="t('shell.theme_toggle')" data-i18n-aria-label="shell.theme_toggle" @click="cycleTheme"><span id="theme-icon" data-theme-icon aria-hidden="true"><NxpIcon name="theme" /></span></NxpIconButton></div></header>
      <DashboardPage v-if="shell.booted && segments[0] === 'dashboard'" />
      <HistoryPage v-else-if="shell.booted && segments[0] === 'history'" />
      <PluginsPage v-else-if="shell.booted && segments[0] === 'plugins'" />
      <QueuesPage v-else-if="shell.booted && segments[0] === 'queues'" />
      <DispatchPage v-else-if="shell.booted && segments[0] === 'dispatch'" />
      <SettingsPage v-else-if="shell.booted && segments[0] === 'settings'" />
      <ScriptsPage v-else-if="shell.booted && segments[0] === 'scripts'" />
      <UsersPage v-else-if="shell.booted && segments[0] === 'users'" />
      <PluginRouteHost v-else-if="shell.booted && segments[0] === 'plugin'" :segments="segments" :ready="shell.booted" />
      <BootLoadingState v-else-if="!shell.bootError" />
    </div>
  </div>
  <div id="toast" class="toast hidden" role="status" aria-live="polite"></div>
  <div id="notice-stack" aria-live="polite" :aria-label="t('shell.page_notifications')" data-i18n-aria-label="shell.page_notifications"></div>
  <div v-if="shell.bootError" class="empty nxp-boot-error" role="alert"><strong>页面加载失败</strong><span>{{ shell.bootError }}</span></div>
</template>
