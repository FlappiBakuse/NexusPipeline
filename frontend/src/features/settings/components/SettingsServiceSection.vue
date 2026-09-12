<script setup lang="ts">
import { computed } from "vue";
import { t } from "../../../platform/i18n";
import NxpCollapsibleCard from "../../../ui/composites/NxpCollapsibleCard.vue";
import NxpNumberInput from "../../../ui/primitives/NxpNumberInput.vue";
import NxpSelect, { type NxpOption } from "../../../ui/primitives/NxpSelect.vue";
import NxpSwitchSetting from "../../../ui/composites/NxpSwitchSetting.vue";
import type { Settings } from "../utils/settingsTypes";

/** 服务行为 section：启动注册、轻量模式、浏览器打开、保留期、端口、日志级别与语言。 */

const props = defineProps<{
  settings: Settings;
  expanded: boolean;
  locale: string;
  localeOptions: NxpOption[];
  save: () => void;
  saveWithRestart: () => void;
  onLightweightChange: () => void;
}>();
const emit = defineEmits<{ toggle: []; changeLocale: [value: string | string[]] }>();

const logLevelOptions = computed<NxpOption[]>(() => [
  { value: "debug", label: t("settings.log_level_debug") },
  { value: "info", label: t("settings.log_level_info") },
  { value: "warn", label: t("settings.log_level_warn") },
  { value: "error", label: t("settings.log_level_error") },
  { value: "fatal", label: t("settings.log_level_fatal") },
]);
</script>

<template>
  <NxpCollapsibleCard
    panel-id="settings-panel-service"
  panel="service"
    data-settings-panel="service"
    data-testid="service-settings"
    :expanded="props.expanded"
    :title="t('settings.service_behavior')"
    :description="t('settings.service.options_help')"
    @toggle="emit('toggle')"
  >
    <div class="settings-list">
      <NxpSwitchSetting
        id="st-autostart"
        v-model="props.settings.autoStart"
        :label="t('settings.start_with_windows')"
        :description="t('settings.service.startup_registration')"
        :aria-label="t('settings.start_with_windows')"
        @change="props.save"
      />
      <NxpSwitchSetting
        id="st-lightweight"
        v-model="props.settings.lightweightMode"
        :label="t('settings.lightweight_mode')"
        :description="t('settings.service.web_disabled')"
        :aria-label="t('settings.lightweight_mode')"
        @change="props.onLightweightChange"
      />
      <NxpSwitchSetting
        id="st-browser"
        v-model="props.settings.autoOpenBrowser"
        :label="t('settings.open_browser')"
        :description="t('settings.service.console_startup')"
        :aria-label="t('settings.open_browser')"
        @change="props.save"
      />
    </div>
    <div class="settings-service-fields" :data-help="t('settings.service.restart_requirements')">
      <div class="form-grid settings-service-grid settings-service-grid-primary">
        <div class="field">
          <label class="field-label" for="st-retention">{{ t("settings.history_retention_days") }}</label>
          <NxpNumberInput
            id="st-retention"
            v-model.number="props.settings.historyRetentionDays"
            :min="1"
            :max="180"
            :aria-label="t('settings.history_retention_days')"
            @change="props.save"
          />
        </div>
        <div class="field">
          <label class="field-label" for="st-port">{{ t("settings.web_port") }}</label>
          <NxpNumberInput
            id="st-port"
            v-model.number="props.settings.webPort"
            :min="1024"
            :max="65535"
            :aria-label="t('settings.web_port')"
            @change="props.saveWithRestart"
          />
        </div>
        <div class="field">
          <label class="field-label" for="st-loglevel-trigger">{{ t("settings.log_level") }}</label>
          <NxpSelect
            id="st-loglevel"
            v-model="props.settings.logLevel"
            :options="logLevelOptions"
            :aria-label="t('settings.log_level')"
            @change="props.save"
          />
        </div>
      </div>
      <div class="form-grid settings-service-grid settings-service-grid-locale">
        <div class="field" :data-help="t('settings.language_help', {}, 'Language preference is stored in this browser only')">
          <label class="field-label" for="settings-locale-trigger">{{ t("settings.language", {}, "Interface language") }}</label>
          <NxpSelect
            id="settings-locale"
            :model-value="props.locale"
            :options="props.localeOptions"
            :aria-label="t('settings.language')"
            @change="emit('changeLocale', $event)"
          />
        </div>
        <div class="field" :data-help="t('settings.host_language_help', {}, 'Controls CLI, tray, notifications, and background logs.')">
          <label class="field-label" for="st-host-locale-trigger">{{ t("settings.host_language", {}, "Host language") }}</label>
          <NxpSelect
            id="st-host-locale"
            v-model="props.settings.hostLocale"
            :options="props.localeOptions"
            :aria-label="t('settings.host_language')"
            @change="props.save"
          />
        </div>
      </div>
    </div>
    <p v-if="props.settings.lightweightMode" class="callout callout-warning">
      {{ t("settings.service.lightweight_not_started") }}
    </p>
  </NxpCollapsibleCard>
</template>
