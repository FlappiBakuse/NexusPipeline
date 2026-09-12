<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from "vue";
import { RouterView, useRoute } from "vue-router";
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
import NxpIconButton from "../ui/primitives/NxpIconButton.vue";
import NxpIcon from "../ui/primitives/NxpIcon.vue";
import NxpScrollArea from "../ui/primitives/NxpScrollArea.vue";

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
let autoScrollObserver: MutationObserver | null = null;
let autoScrollFrame: number | null = null;

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
      <NxpScrollArea class="page-main-scroll" :aria-label="t('shell.main_content')">
        <Transition name="nxp-page" mode="out-in" appear>
          <RouterView v-if="shell.booted" :key="route.fullPath" />
          <BootLoadingState v-else-if="!shell.bootError" />
        </Transition>
      </NxpScrollArea>
    </div>
  </div>
  <div id="toast" class="toast hidden" role="status" aria-live="polite"></div>
  <div id="notice-stack" aria-live="polite" :aria-label="t('shell.page_notifications')" data-i18n-aria-label="shell.page_notifications"></div>
  <div v-if="shell.bootError" class="empty nxp-boot-error" role="alert"><strong>{{ t("shell.boot.error_details") }}</strong><span>{{ shell.bootError }}</span></div>
  <TokenPrompt :open="shell.tokenPromptOpen" @close="shell.tokenPromptOpen = false" />
</template>
