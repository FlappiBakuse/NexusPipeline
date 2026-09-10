<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, watch } from "vue";
import { useRoute } from "vue-router";
import { useShellStore } from "../stores/shell";
import LegacyPageHost from "./LegacyPageHost";
import DashboardPage from "../features/dashboard/DashboardPage.vue";
import { bootstrapFrontend } from "../legacy/bootstrap";
import { installLegacyEventBridge } from "../legacy/event-bridge";
import { applyTranslations } from "@legacy/core/i18n.js";
import { setNavOpen } from "@legacy/core/ui.js";
import { cycleTheme } from "@legacy/core/ui.js";
import NxpIconButton from "../ui/primitives/NxpIconButton.vue";

const route = useRoute();
const shell = useShellStore();
const segments = computed(() => {
  const path = String(route.path || "/dashboard").replace(/^\/+|\/+$/g, "");
  return (path || "dashboard").split("/").filter(Boolean);
});
const navigation = [
  ["dashboard", "▦", "shell.dashboard"],
  ["users", "♙", "shell.users"],
  ["scripts", "□", "shell.scripts"],
  ["queues", "≡", "shell.queues"],
  ["dispatch", "▶", "shell.dispatch"],
  ["history", "◷", "shell.history"],
  ["plugins", "✦", "shell.plugins"],
  ["settings", "⚙", "shell.settings"],
] as const;

function closeOnBackdrop(event: Event) {
  const target = event.target instanceof Element ? event.target : null;
  if (target?.closest(".nav-backdrop")) shell.navOpen = false;
}

function closeOnDesktopResize() {
  if (window.innerWidth > 820 && shell.navOpen) closeNav();
}

watch(() => route.fullPath, () => {
  if (shell.navOpen) closeNav();
});

onBeforeUnmount(() => {
  document.removeEventListener("click", closeOnBackdrop, true);
  window.removeEventListener("resize", closeOnDesktopResize);
  setNavOpen(false);
});

onMounted(async () => {
  document.addEventListener("click", closeOnBackdrop, true);
  window.addEventListener("resize", closeOnDesktopResize);
  installLegacyEventBridge();
  try {
    const booted = await bootstrapFrontend();
    if (booted) {
      shell.markBooted();
      applyTranslations();
    }
  } catch (error) {
    shell.markBootError(error);
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
    <aside id="sidebar" class="sidebar" aria-label="主导航" :aria-hidden="shell.navOpen ? 'false' : 'true'">
      <div class="brand"><div class="brand-mark" aria-hidden="true">N</div><div class="brand-copy"><strong>NexusPipeline</strong><span data-i18n="shell.brand_suffix"></span></div></div>
      <div class="nav-caption" data-i18n="shell.workbench"></div>
      <nav class="main-nav" aria-label="工作台">
        <a v-for="[path, icon, label] in navigation.slice(0, 6)" :key="path" :href="`#/${path}`" :data-page="path" :data-testid="`nav-${path}`" :class="{ active: segments[0] === path }" :aria-current="segments[0] === path ? 'page' : undefined" @click="closeNav"><span class="nav-icon" aria-hidden="true">{{ icon }}</span><span :data-i18n="label"></span></a>
      </nav>
      <div class="nav-caption nav-caption-bottom" data-i18n="shell.system"></div>
      <nav class="main-nav" aria-label="系统">
        <a v-for="[path, icon, label] in navigation.slice(6)" :key="path" :href="`#/${path}`" :data-page="path" :data-testid="`nav-${path}`" :class="{ active: segments[0] === path }" :aria-current="segments[0] === path ? 'page' : undefined" @click="closeNav"><span class="nav-icon" aria-hidden="true">{{ icon }}</span><span :data-i18n="label"></span></a>
      </nav>
      <nav class="main-nav plugin-nav-host" data-plugin-anchor="shell.nav" aria-label="插件导航"></nav>
      <div class="sidebar-foot"><div class="sidebar-foot-copy"><span id="local-addr" data-testid="local-addr" data-i18n="shell.local_service"></span><span id="app-version" data-i18n="shell.current_version"></span></div><NxpIconButton label="切换主题" @click="cycleTheme"><span data-theme-icon aria-hidden="true">◐</span></NxpIconButton></div>
    </aside>
    <div class="nav-backdrop" @click.capture="closeNav"><button type="button" aria-label="关闭导航" @pointerdown="closeNav" @click.stop="closeNav"></button></div>
    <div class="page-shell">
      <header class="topbar"><NxpIconButton class="menu-button" label="打开导航" :expanded="shell.navOpen" aria-controls="sidebar" @click="openNav">☰</NxpIconButton><div class="topbar-context"><span class="topbar-product" data-i18n="shell.product"></span><span id="topbar-title" class="sr-only" data-i18n="shell.dashboard"></span></div><div class="topbar-actions"><NxpIconButton label="切换主题" @click="cycleTheme"><span id="theme-icon" data-theme-icon aria-hidden="true">◐</span></NxpIconButton></div></header>
      <DashboardPage v-if="shell.booted && segments[0] === 'dashboard'" />
      <LegacyPageHost v-else :segments="segments" :ready="shell.booted" />
    </div>
  </div>
  <div id="toast" class="toast hidden" role="status" aria-live="polite"></div>
  <div id="notice-stack" aria-live="polite" aria-label="页面通知"></div>
  <div v-if="shell.bootError" class="empty nxp-boot-error" role="alert"><strong>页面加载失败</strong><span>{{ shell.bootError }}</span></div>
</template>
