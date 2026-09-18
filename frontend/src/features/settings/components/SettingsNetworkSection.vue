<script setup lang="ts">
import { computed } from "vue";
import { t } from "../../../platform/i18n";
import NxpField from "../../../ui/primitives/NxpField.vue";
import NxpSelect, { type NxpOption } from "../../../ui/primitives/NxpSelect.vue";
import NxpTextInput from "../../../ui/primitives/NxpTextInput.vue";
import NxpCollapsibleCard from "../../../ui/composites/NxpCollapsibleCard.vue";
import type { Settings } from "../utils/settingsTypes";

/** 网络代理 section：外部请求出口的代理模式与凭据表单；保存事务由页面统一调度。 */

const props = defineProps<{
  settings: Settings;
  expanded: boolean;
  secretDraft: Record<string, string>;
  save: () => void;
}>();
const emit = defineEmits<{ toggle: [] }>();

const proxyModeOptions = computed<NxpOption[]>(() => [
  { value: "none", label: t("settings.no_proxy") },
  { value: "system", label: t("settings.use_system_settings") },
  { value: "http", label: t("settings.http_https_proxy") },
]);
</script>

<template>
  <NxpCollapsibleCard
    data-settings-panel="network"
    panel="network"
    panel-id="settings-panel-network"
    :title="t('settings.network_proxy')"
    :description="t('settings.network.external_requests')"
    :expanded="expanded"
    @toggle="emit('toggle')"
  >
      <div class="network-settings" :data-help="t('settings.network_proxy_help')">
        <NxpField :label="t('settings.proxy_mode')" for="st-proxy-mode-trigger">
          <NxpSelect
            id="st-proxy-mode"
            v-model="settings.proxyMode"
            :options="proxyModeOptions"
            :aria-label="t('settings.proxy_mode')"
            @change="save"
          />
        </NxpField>
        <div
          v-show="settings.proxyMode === 'http'"
          id="st-proxy-custom"
          class="proxy-custom-fields"
        >
          <NxpField :label="t('settings.http_https_proxy_address')" for="st-proxy-url" :help="t('settings.validation.proxy_scheme')">
            <NxpTextInput
              id="st-proxy-url"
              v-model="settings.proxyUrl"
              :aria-label="t('settings.http_https_proxy_address')"
              placeholder="http://127.0.0.1:7890"
              @blur="save"
            />
          </NxpField>
          <NxpField :label="t('settings.username_optional')" for="st-proxy-user">
            <NxpTextInput
              id="st-proxy-user"
              v-model="settings.proxyUsername"
              :aria-label="t('settings.username_optional')"
              @blur="save"
            />
          </NxpField>
          <NxpField :label="t('settings.password_optional')" for="st-proxy-pwd" :help="t('settings.network.proxy_password_keep')">
            <NxpTextInput
              id="st-proxy-pwd"
              v-model="secretDraft.proxyPassword"
              type="password"
              :aria-label="t('settings.password_optional')"
              @blur="save"
            />
          </NxpField>
        </div>
      </div>
  </NxpCollapsibleCard>

</template>
