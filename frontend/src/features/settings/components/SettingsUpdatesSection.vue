<script setup lang="ts">
import { computed, ref } from "vue";
import { t } from "../../../platform/i18n";
import NxpCollapsibleCard from "../../../ui/composites/NxpCollapsibleCard.vue";
import NxpSelect, { type NxpOption } from "../../../ui/primitives/NxpSelect.vue";
import NxpSwitch from "../../../ui/primitives/NxpSwitch.vue";
import UpdateStatusCard from "./UpdateStatusCard.vue";
import type { Settings } from "../utils/settingsTypes";

/** 更新 section：检查频率、自动应用、更新通道、镜像地址与更新状态卡片。 */

const props = defineProps<{
  settings: Settings;
  expanded: boolean;
  save: () => void;
  onUpdateCheck: (value: boolean) => void;
}>();
const emit = defineEmits<{ toggle: []; "update:updateAutoApplyEnabled": [value: boolean] }>();

const updateCard = ref<InstanceType<typeof UpdateStatusCard> | null>(null);
const updateChannelOptions = computed<NxpOption[]>(() => [
  { value: "prerelease", label: t("settings.pre_release") },
  { value: "stable", label: t("settings.stable") },
]);

function reload() {
  return updateCard.value?.reload();
}

defineExpose({ reload });
</script>

<template>
  <NxpCollapsibleCard
    panel-id="settings-panel-updates"
  panel="updates"
    data-settings-panel="updates"
    data-testid="update-section"
    :expanded="props.expanded"
    :title="t('settings.update_settings')"
    :description="t('settings.update.channel_help')"
    @toggle="emit('toggle')"
  >
    <div class="update-section">
      <div class="settings-list">
        <div class="switch-row settings-option switch-card">
          <div>
            <strong>{{ t("settings.update.periodic") }}</strong><span class="muted">{{ t("settings.update.check_schedule") }}</span>
          </div>
          <NxpSwitch
            id="st-update-check"
            :model-value="props.settings.updateCheckEnabled === true"
            :aria-label="t('settings.update.periodic')"
            @update:model-value="props.onUpdateCheck"
          />
        </div>
        <div class="switch-row settings-option switch-card">
          <div>
            <strong>{{ t("settings.update_automatically_when_idle") }}</strong><span class="muted">{{ t("settings.update.auto_download_help") }}</span>
          </div>
          <NxpSwitch
            id="st-update-auto"
            :model-value="props.settings.updateAutoApplyEnabled === true"
            :disabled="props.settings.updateCheckEnabled !== true"
            :aria-label="t('settings.update_automatically_when_idle')"
            @update:model-value="emit('update:updateAutoApplyEnabled', $event)"
            @change="props.save"
          />
        </div>
      </div>
      <div class="form-grid">
        <div class="field">
          <label class="field-label" for="st-update-channel-trigger">{{ t("settings.update_channel") }}</label>
          <NxpSelect
            id="st-update-channel"
            v-model="props.settings.updateChannel"
            :options="updateChannelOptions"
            :aria-label="t('settings.update_channel')"
            @change="props.save"
          />
        </div>
        <div class="field" :data-help="t('settings.update.source_default_help')">
          <label class="field-label" for="st-update-source">{{ t("settings.mirror_url") }}</label>
          <input
            id="st-update-source"
            v-model="props.settings.updateSourceUrl"
            :placeholder="t('settings.default_github')"
            @blur="props.save"
          >
        </div>
      </div>
      <UpdateStatusCard ref="updateCard" />
    </div>
  </NxpCollapsibleCard>
</template>
