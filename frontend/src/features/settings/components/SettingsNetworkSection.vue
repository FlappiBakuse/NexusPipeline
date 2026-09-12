<script setup lang="ts">
import { computed } from "vue";
import { t } from "../../../platform/i18n";
import NxpIcon from "../../../ui/primitives/NxpIcon.vue";
import NxpSelect, { type NxpOption } from "../../../ui/primitives/NxpSelect.vue";
import NxpTextInput from "../../../ui/primitives/NxpTextInput.vue";
import NxpCollapseTransition from "../../../ui/composites/NxpCollapseTransition.vue";
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
  <section
    class="settings-card section-surface"
    :class="{ 'is-expanded': expanded }"
    data-settings-panel="network"
  >
    <button
      class="settings-card-toggle"
      type="button"
      data-action="toggle-settings-panel"
      data-panel="network"
      :aria-expanded="expanded"
      aria-controls="settings-panel-network"
      @click.stop="emit('toggle')"
    >
      <span class="settings-card-copy"
        ><strong class="settings-card-title">{{
          t("settings.network_proxy")
        }}</strong
        ><span class="muted">{{
          t("settings.network.external_requests")
        }}</span></span
      ><span class="settings-card-arrow" aria-hidden="true"
        ><NxpIcon
          :name="
            expanded ? 'chevronDown' : 'chevronRight'
          "
          class-name="settings-card-arrow-icon"
      /></span>
    </button>
    <NxpCollapseTransition>
      <div
        id="settings-panel-network"
        class="settings-card-body"
        v-show="expanded"
      >
      <div class="network-settings" :data-help="t('settings.network_proxy_help')">
        <div class="field">
          <label class="field-label" for="st-proxy-mode-trigger">{{
            t("settings.proxy_mode")
          }}</label
          ><NxpSelect
            id="st-proxy-mode"
            v-model="settings.proxyMode"
            :options="proxyModeOptions"
            :aria-label="t('settings.proxy_mode')"
            @change="save"
          />
        </div>
        <div
          v-show="settings.proxyMode === 'http'"
          id="st-proxy-custom"
          class="proxy-custom-fields"
        >
          <div class="field" :data-help="t('settings.validation.proxy_scheme')">
            <label class="field-label" for="st-proxy-url">{{
              t("settings.http_https_proxy_address")
            }}</label
            ><NxpTextInput
              id="st-proxy-url"
              v-model="settings.proxyUrl"
              :aria-label="t('settings.http_https_proxy_address')"
              placeholder="http://127.0.0.1:7890"
              @blur="save"
            />
          </div>
          <div class="field">
            <label class="field-label" for="st-proxy-user">{{
              t("settings.username_optional")
            }}</label
            ><NxpTextInput
              id="st-proxy-user"
              v-model="settings.proxyUsername"
              :aria-label="t('settings.username_optional')"
              @blur="save"
            />
          </div>
          <div class="field" :data-help="t('settings.network.proxy_password_keep')">
            <label class="field-label" for="st-proxy-pwd">{{
              t("settings.password_optional")
            }}</label
            ><NxpTextInput
              id="st-proxy-pwd"
              v-model="secretDraft.proxyPassword"
              type="password"
              :aria-label="t('settings.password_optional')"
              @blur="save"
            />
          </div>
        </div>
      </div>
      </div>
    </NxpCollapseTransition>
  </section>

</template>
