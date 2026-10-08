<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, provide, ref, watch } from "vue";
import { RouterLink, RouterView, useRoute } from "vue-router";
import { useShellStore } from "../stores/shell";
import BootLoadingState from "./BootLoadingState.vue";
import TokenPrompt from "./TokenPrompt.vue";
import { bootstrapFrontend } from "./bootstrap";
import { applyTranslations, t } from "../platform/i18n";
import { setNavOpen } from "../platform/shell";
import { cycleTheme } from "../platform/shell";
import { enterPage } from "../platform/page-state";
import { isAbortError } from "../platform/api";
import { initAutoScroll } from "../platform/auto-scroll";
import { registerTokenPromptRenderer } from "../platform/auth";
import { resumeServiceRecovery, startServiceObserver } from "../platform/service-recovery";
import NxpButton from "../ui/primitives/NxpButton.vue";
import NxpIconButton from "../ui/primitives/NxpIconButton.vue";
import NxpIcon from "../ui/primitives/NxpIcon.vue";
import NxpScrollArea from "../ui/primitives/NxpScrollArea.vue";
import NxpEmptyState from "../ui/primitives/NxpEmptyState.vue";
import {desktopBridge} from '../platform/desktop';
import {startApplicationEventStream} from '../platform/events';
import {browserPageRefresh,receiveBrowserPageRefresh,startPageRefresh,dismissPageRefresh,confirmBrowserPageRefresh} from '../platform/page-refresh';
import NxpConfirmDialog from '../ui/composites/NxpConfirmDialog.vue';

import ConfigEditFlow from "../features/users/components/ConfigEditFlow.vue";
import { configEditContextKey } from "../features/users/composables/configEditContext";

const configFlow = ref<InstanceType<typeof ConfigEditFlow> | null>(null);
const configEditCompletion = ref<{ userId: string; revision: number } | null>(null);
provide(configEditContextKey, { flow: configFlow, completed: configEditCompletion });
function configurationChanged(userId: string) {
  configEditCompletion.value = { userId, revision: (configEditCompletion.value?.revision || 0) + 1 };
}
const route = useRoute();
const shell = useShellStore();
const desktop=desktopBridge();
const shutdownNotice=ref(false);
let stopShutdownNotice: (()=>void)|null=null;
const isMobile = ref(false);
const segments = computed(() => {
  const path = String(route.path || "/dashboard").replace(/^\/+|\/+$/g, "");
  return (path || "dashboard").split("/").filter(Boolean);
});
const navigation = [
  ["dashboard", "dashboard", "shell.dashboard"],
  ["dispatch", "dispatch", "shell.dispatch"],
  ["queues", "queues", "shell.queues"],
  ["scripts", "scripts", "shell.scripts"],
  ["users", "users", "shell.users"],
  ["history", "history", "shell.history"],
  ["plugins", "plugins", "shell.plugins"],
  ["settings", "settings", "shell.settings"],
] as const;
const preferencesUnavailable = document.documentElement.dataset.desktopPreferences === 'unavailable';
let autoScrollObserver: MutationObserver | null = null;
let autoScrollFrame: number | null = null;
let stopServiceObserver: (() => void) | null = null;
let stopEvents: (()=>void)|null=null;
let stopRefresh: (()=>void)|null=null;
const localAddress = computed(() => shell.actualPort > 0
  ? t("service.with_host", { host: location.hostname, port: shell.actualPort })
  : t("service.label"));
const recoveryMessage = computed(() => {
  if (shell.recoveryPhase === "dirty-blocked") return t("shell.recovery.dirty");
  if (shell.recoveryPhase === "timeout") return t("shell.recovery.timeout");
  if (shell.recoveryPhase === "failed") return t("shell.recovery.failed");
  return t("shell.recovery.waiting");
});

function scheduleAutoScroll() {
  if (autoScrollFrame !== null) return;
  const refresh = () => {
    autoScrollFrame = null;
    const pageShell = document.querySelector<HTMLElement>(".page-shell");
    if (pageShell) initAutoScroll(pageShell);
  };
  if (typeof window.requestAnimationFrame === "function") {
    autoScrollFrame = window.requestAnimationFrame(refresh);
  } else {
    refresh();
  }
}

function closeOnBackdrop(event: Event) {
  const target = event.target instanceof Element ? event.target : null;
  if (target?.closest(".nav-backdrop")) shell.navOpen = false;
}

function closeOnDesktopResize() {
  isMobile.value = window.innerWidth <= 820;
  if (window.innerWidth > 820 && shell.navOpen) closeNav();
}

// 路由提交后终止上一页面的轮询/请求代际，并关闭移动端抽屉。
watch(() => route.fullPath, () => {
  enterPage(segments.value[0] || "dashboard");
  if (shell.navOpen) closeNav();
});

onBeforeUnmount(() => {
  stopEvents?.();stopRefresh?.();
  stopShutdownNotice?.();
  stopServiceObserver?.();
  stopServiceObserver = null;
  autoScrollObserver?.disconnect();
  autoScrollObserver = null;
  if (autoScrollFrame !== null && typeof window.cancelAnimationFrame === "function") {
    window.cancelAnimationFrame(autoScrollFrame);
    autoScrollFrame = null;
  }
  window.removeEventListener("resize", scheduleAutoScroll);
  document.removeEventListener("click", closeOnBackdrop, true);
  window.removeEventListener("resize", closeOnDesktopResize);
  registerTokenPromptRenderer(null);
  setNavOpen(false);
});

onMounted(async () => {
  stopShutdownNotice=desktop?.onShutdownNotice(()=>{shutdownNotice.value=true;})??null;
  registerTokenPromptRenderer(() => {
    shell.tokenPromptOpen = true;
  });
  closeOnDesktopResize();
  document.addEventListener("click", closeOnBackdrop, true);
  window.addEventListener("resize", closeOnDesktopResize);
  window.addEventListener("resize", scheduleAutoScroll);
  const pageShell = document.querySelector<HTMLElement>(".page-shell");
  if (pageShell && typeof MutationObserver !== "undefined") {
    autoScrollObserver = new MutationObserver(scheduleAutoScroll);
    autoScrollObserver.observe(pageShell, { childList: true, subtree: true });
  }
  scheduleAutoScroll();
  try {
    const booted = await bootstrapFrontend();
    if (booted) {
      shell.markBooted();
      applyTranslations();
      scheduleAutoScroll();
      stopServiceObserver = startServiceObserver(shell);
      stopRefresh=startPageRefresh();
      stopEvents=startApplicationEventStream(event=>receiveBrowserPageRefresh(event.data));
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
  <div v-if="desktop" class="desktop-titlebar"><span>NexusPipeline</span><span class="desktop-connection" role="status">{{ shell.identityConnection === 'offline' ? t('shell.recovery.disconnected') : '' }}</span></div>
  <div class="app-shell" :class="{ 'nav-open': shell.navOpen, 'desktop-shell': !!desktop }" :inert="shell.recoveryPhase === 'navigating' || shutdownNotice">
    <aside id="sidebar" class="sidebar" :aria-label="t('shell.nav.main')" data-i18n-aria-label="shell.nav.main" :inert="isMobile && !shell.navOpen" :aria-hidden="isMobile ? (shell.navOpen ? 'false' : 'true') : undefined">
      <div class="brand"><div class="brand-mark" aria-hidden="true">N</div><div class="brand-copy"><strong>NexusPipeline</strong><span data-i18n="shell.brand_suffix"></span></div></div>
      <div class="sidebar-navigation">
      <div class="nav-caption" data-i18n="shell.workbench"></div>
        <nav class="main-nav" :aria-label="t('shell.workbench')" data-i18n-aria-label="shell.workbench">
        <RouterLink v-for="[path, icon, label] in navigation.slice(0, 3)" :key="path" :to="`/${path}`" :data-page="path" :data-testid="`nav-${path}`" :class="{ active: segments[0] === path }" :aria-current="segments[0] === path ? 'page' : undefined" @click="closeNav"><span class="nav-icon"><NxpIcon :name="icon" /></span><span :data-i18n="label"></span></RouterLink>
      </nav>
      <div class="nav-caption" data-i18n="shell.management"></div>
      <nav class="main-nav" :aria-label="t('shell.management')">
        <RouterLink v-for="[path, icon, label] in navigation.slice(3, 6)" :key="path" :to="`/${path}`" :data-page="path" :data-testid="`nav-${path}`" :class="{ active: segments[0] === path }" :aria-current="segments[0] === path ? 'page' : undefined" @click="closeNav"><span class="nav-icon"><NxpIcon :name="icon" /></span><span :data-i18n="label"></span></RouterLink>
      </nav>
      <div class="nav-caption nav-caption-bottom" data-i18n="shell.system"></div>
      <nav class="main-nav plugin-nav-host" data-plugin-anchor="shell.nav" :aria-label="t('shell.nav.plugins')" data-i18n-aria-label="shell.nav.plugins"></nav>
      <nav class="main-nav" :aria-label="t('shell.system')" data-i18n-aria-label="shell.system">
        <RouterLink v-for="[path, icon, label] in navigation.slice(6)" :key="path" :to="`/${path}`" :data-page="path" :data-testid="`nav-${path}`" :class="{ active: segments[0] === path }" :aria-current="segments[0] === path ? 'page' : undefined" @click="closeNav"><span class="nav-icon"><NxpIcon :name="icon" /></span><span :data-i18n="label"></span></RouterLink>
      </nav>
      </div>
      <div class="sidebar-foot"><div class="sidebar-foot-copy"><span id="local-addr" data-testid="local-addr">{{ localAddress }}</span><span id="app-version" :title="shell.identityConnection === 'online' ? '' : t('shell.recovery.disconnected')">{{ t('common.current_version') }} · {{ shell.hostVersion || t('shell.version_unknown') }}</span></div><NxpIconButton :label="t('shell.theme_toggle')" data-i18n-aria-label="shell.theme_toggle" @click="cycleTheme"><span data-theme-icon aria-hidden="true"><NxpIcon name="theme" /></span></NxpIconButton></div>
    </aside>
    <div class="nav-backdrop" @click.capture="closeNav"><button type="button" :aria-label="t('shell.close_navigation')" data-i18n-aria-label="shell.close_navigation" @pointerdown="closeNav" @click.stop="closeNav"></button></div>
    <div class="page-shell">
      <header class="topbar"><NxpIconButton class="menu-button" :label="t('shell.open_navigation')" data-i18n-aria-label="shell.open_navigation" :expanded="shell.navOpen" aria-controls="sidebar" @click="openNav"><NxpIcon name="menu" /></NxpIconButton><div class="topbar-context"><span class="topbar-product" data-i18n="shell.product"></span><span id="topbar-title" class="sr-only" data-i18n="shell.dashboard"></span></div><div class="topbar-actions"><NxpIconButton :label="t('shell.theme_toggle')" data-i18n-aria-label="shell.theme_toggle" @click="cycleTheme"><span id="theme-icon" data-theme-icon aria-hidden="true"><NxpIcon name="theme" /></span></NxpIconButton></div></header>
      <NxpScrollArea class="page-main-scroll" :aria-label="t('shell.main_content')">
        <p v-if="preferencesUnavailable" class="desktop-preferences-warning" role="status">{{ t('shell.desktop.preferences_unavailable') }}</p>
        <RouterView v-if="shell.booted" v-slot="{ Component, route: viewRoute }">
          <div :key="viewRoute.fullPath" class="app-page">
            <component :is="Component" />
          </div>
        </RouterView>
        <BootLoadingState v-else-if="!shell.bootError" />
      </NxpScrollArea>
    </div>
  </div>
  <div id="toast" class="toast hidden" role="status" aria-live="polite"></div>
  <div id="notice-stack" aria-live="polite" :aria-label="t('shell.page_notifications')" data-i18n-aria-label="shell.page_notifications"></div>
  <div v-if="shutdownNotice" class="dashboard-system-note" role="alert">{{ t('shell.desktop.asset_update') }}</div>
  <div v-if="shell.recoveryPhase !== 'idle' && shell.recoveryPhase !== 'navigating'" class="dashboard-system-note" role="status" aria-live="polite">
    <span>{{ recoveryMessage }}</span>
    <NxpButton v-if="['timeout', 'failed', 'dirty-blocked'].includes(shell.recoveryPhase)" class="ghost" type="button" @click="resumeServiceRecovery(shell.recoveryPhase === 'dirty-blocked')">
      {{ shell.recoveryPhase === 'dirty-blocked' ? t('shell.recovery.discard_refresh') : t('shell.recovery.continue') }}
    </NxpButton>
  </div>
  <NxpEmptyState
    v-if="shell.bootError"
    class="app-boot-error"
    role="alert"
    tone="danger"
    :title="t('shell.boot.error_details')"
    :description="shell.bootError"
  />
  <ConfigEditFlow v-if="shell.booted" ref="configFlow" @changed="configurationChanged" />
  <TokenPrompt :open="shell.tokenPromptOpen" @close="shell.tokenPromptOpen = false" />
  <NxpConfirmDialog :open="!!browserPageRefresh" :title="t('shell.page_refresh.title')" :message="t('shell.page_refresh.confirm')"
    :confirm-label="t('shell.page_refresh.reload')" :cancel-label="t('common.cancel')" confirm-tone="danger"
    @close="dismissPageRefresh" @cancel="dismissPageRefresh" @confirm="confirmBrowserPageRefresh" />
</template>
