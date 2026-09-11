<script setup lang="ts">
import { computed } from "vue";
import { t } from "../../platform/i18n";

const props = withDefaults(defineProps<{
  page?: number;
  totalPages?: number;
  total?: number;
  pageSize?: number;
  label?: string;
  previousLabel?: string;
  nextLabel?: string;
}>(), {
  page: 1,
  totalPages: 1,
  total: 0,
  pageSize: 20,
  label: "分页",
  previousLabel: "",
  nextLabel: "",
});
const emit = defineEmits<{ pageChange: [page: number]; "update:page": [page: number] }>();

const range = computed(() => {
  if (!props.total) return "";
  const from = (props.page - 1) * props.pageSize + 1;
  const to = Math.min(props.total, props.page * props.pageSize);
  return t("common.pager.range", { from, to }, `, ${from}-${to}`);
});
const summary = computed(() => t("common.pager.summary", { total: props.total, range: range.value }, `${props.total}`));
const previousText = computed(() => props.previousLabel || t("common.previous", {}, "Previous"));
const nextText = computed(() => props.nextLabel || t("common.next", {}, "Next"));

function go(page: number) {
  const next = Math.max(1, Math.min(props.totalPages, page));
  if (next === props.page) return;
  emit("update:page", next);
  emit("pageChange", next);
}
</script>

<template>
  <nav v-if="props.totalPages > 1" class="pager nxp-pager" :aria-label="props.label" data-testid="nxp-pager" :data-page-current="props.page" :data-pages="props.totalPages">
    <span class="pager-info" aria-live="polite">{{ summary }}</span>
    <div class="nxp-pager-controls">
      <button class="sm" type="button" :disabled="props.page <= 1" @click="go(props.page - 1)">{{ previousText }}</button>
      <button
        v-for="pageNumber in props.totalPages"
        :key="pageNumber"
        class="sm"
        :class="{ 'pager-active': pageNumber === props.page }"
        type="button"
        :aria-current="pageNumber === props.page ? 'page' : undefined"
        @click="go(pageNumber)"
      >{{ pageNumber }}</button>
      <button class="sm" type="button" :disabled="props.page >= props.totalPages" @click="go(props.page + 1)">{{ nextText }}</button>
    </div>
  </nav>
</template>

<style>
.nxp-pager { width: 100%; margin: 0; padding: var(--nx-space-3) var(--nx-space-4) var(--nx-space-4); border-top: 1px solid var(--nx-color-border); }
.nxp-pager-controls { display: inline-flex; align-items: center; flex-wrap: wrap; gap: var(--nx-space-2); }
.nxp-pager button { min-width: 36px; }
</style>
