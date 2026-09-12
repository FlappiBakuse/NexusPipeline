<script setup lang="ts">
import { t } from "../../../platform/i18n";
import { isPluginViewStateActive, type PluginViewState } from "../../../platform/plugin-list";
import NxpDialogPopover from "../../../ui/composites/NxpDialogPopover.vue";
import NxpIcon from "../../../ui/primitives/NxpIcon.vue";

/** 插件页筛选与排序浮层：二级页面语义的 `role="dialog"`，关闭入口由 NxpDialogPopover 提供。 */

defineProps<{
  open: boolean;
  view: PluginViewState;
}>();

const emit = defineEmits<{
  close: [];
  update: [patch: Partial<PluginViewState>];
  reset: [];
}>();
</script>

<template>
  <NxpDialogPopover
    :open="open"
    id="plugin-filter-popover"
    class="plugin-filter-popover"
    :aria-label="t('plugins.plugin_filters_and_sorting')"
    :close-label="t('common.close')"
    @close="emit('close')"
  >
    <fieldset class="plugin-filter-section">
      <legend>{{ t("plugins.plugin_type") }}</legend>
      <div class="plugin-filter-options" role="radiogroup">
        <button
          v-for="[key, label] in [['all', t('common.all')], ['managed-code', t('plugins.general_plugin')], ['data-specialized', t('common.specialized_plugin')]]"
          :key="key"
          class="plugin-filter-option"
          :class="{ 'is-selected': view.kind === key }"
          type="button"
          role="radio"
          :aria-checked="view.kind === key"
          :data-testid="`plugin-filter-kind-${key}`"
          @click="emit('update', { kind: key })"
        >
          {{ label }}<NxpIcon v-if="view.kind === key" name="check" class-name="plugin-filter-option-check" />
        </button>
      </div>
    </fieldset>
    <fieldset class="plugin-filter-section">
      <legend>{{ t("plugins.sort") }}</legend>
      <div class="plugin-filter-options" role="radiogroup">
        <button
          v-for="[key, label] in [['name', t('plugins.name_initial_or_pinyin')], ['createdAt', t('plugins.created_at')], ['updatedAt', t('plugins.updated_at')]]"
          :key="key"
          class="plugin-filter-option"
          :class="{ 'is-selected': view.sortBy === key }"
          type="button"
          role="radio"
          :aria-checked="view.sortBy === key"
          :data-testid="`plugin-filter-sort-${key}`"
          @click="emit('update', { sortBy: key })"
        >
          {{ label }}<NxpIcon v-if="view.sortBy === key" name="check" class-name="plugin-filter-option-check" />
        </button>
      </div>
      <div class="plugin-filter-direction" role="radiogroup">
        <button
          v-for="direction in ['asc', 'desc']"
          :key="direction"
          class="plugin-filter-option"
          :class="{ 'is-selected': view.direction === direction }"
          type="button"
          role="radio"
          :aria-checked="view.direction === direction"
          :data-testid="`plugin-filter-direction-${direction}`"
          @click="emit('update', { direction })"
        >
          {{ direction === 'asc' ? t('plugins.ascending') : t('plugins.descending') }}<NxpIcon v-if="view.direction === direction" name="check" class-name="plugin-filter-option-check" />
        </button>
      </div>
    </fieldset>
    <button
      v-if="isPluginViewStateActive(view)"
      class="plugin-filter-reset ghost"
      type="button"
      data-testid="plugin-filter-reset"
      @click="emit('reset')"
    >
      {{ t("plugins.reset") }}
    </button>
  </NxpDialogPopover>
</template>
