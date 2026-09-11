<script setup lang="ts">
import { t } from "../platform/i18n";
import NxpSpinner from "../ui/primitives/NxpSpinner.vue";
</script>

<template>
  <main id="view" class="view-root loading-state" data-testid="boot-loading" aria-live="polite">
    <NxpSpinner />
    <span>{{ t("common.loading", {}, "正在连接本地服务...") }}</span>
  </main>
</template>
