<script setup lang="ts">
import { t } from "../../../platform/i18n";
import NxpCollapsibleCard from "../../../ui/composites/NxpCollapsibleCard.vue";
import NxpNumberInput from "../../../ui/primitives/NxpNumberInput.vue";
import NxpSwitch from "../../../ui/primitives/NxpSwitch.vue";
import type { Settings } from "../utils/settingsTypes";

/** 远程访问与 MCP section：访问令牌、远程地址列表、MCP 端口与开关。 */

const props = defineProps<{
  settings: Settings;
  expanded: boolean;
  token: string;
  tokenVisible: boolean;
  remoteAddresses: string[];
  save: () => void;
  saveWithRestart: () => void;
  onRemoteChange: (value: boolean) => void;
  onMcpChange: (value: boolean) => void;
  generateToken: () => void;
  copyToken: () => void;
}>();
const emit = defineEmits<{ toggle: []; "update:token": [value: string]; "update:tokenVisible": [value: boolean] }>();
</script>

<template>
  <NxpCollapsibleCard
    panel-id="settings-panel-remote-mcp"
  panel="remote-mcp"
    data-settings-panel="remote-mcp"
    data-testid="mcp-settings"
    :expanded="props.expanded"
    :title="t('settings.remote_access.mcp')"
    :description="t('settings.remote_access.management_help')"
    @toggle="emit('toggle')"
  >
    <div class="settings-merged-content">
      <section class="settings-subsection remote-settings">
        <div class="settings-list">
          <div class="switch-row settings-option switch-card">
            <div>
              <strong>{{ t("settings.remote_access") }}</strong><span class="muted">{{ t("settings.remote_access.bind_all_help") }}</span>
            </div>
            <NxpSwitch
              id="st-remote"
              :model-value="props.settings.allowRemoteAccess === true"
              :aria-label="t('settings.remote_access')"
              @update:model-value="props.onRemoteChange"
            />
          </div>
        </div>
        <div class="field-btn-row">
          <div class="field" :data-help="t('settings.remote_access.token_keep_help')">
            <label class="field-label" for="st-token">{{ t("settings.access_token") }}</label>
            <input
              id="st-token"
              :value="props.token"
              :type="props.tokenVisible ? 'text' : 'password'"
              autocomplete="new-password"
              :placeholder="t('common.leave_blank_to_keep')"
              @input="emit('update:token', ($event.target as HTMLInputElement).value)"
              @change="props.save"
            >
          </div>
          <button
            class="ghost"
            type="button"
            data-testid="toggle-token-visibility"
            :aria-pressed="props.tokenVisible"
            @click="emit('update:tokenVisible', !props.tokenVisible)"
          >
            {{ props.tokenVisible ? t("settings.hide") : t("common.show") }}
          </button>
          <button class="ghost" type="button" @click="props.copyToken">{{ t("settings.copy") }}</button>
          <button class="ghost" type="button" data-testid="gen-token" @click="props.generateToken">
            {{ t("settings.generate_token") }}
          </button>
        </div>
        <div
          id="remote-lan-list"
          class="detail"
          :data-help="props.settings.allowRemoteAccess ? t('settings.remote_access.lan_help_short') : undefined"
        >
          <div v-for="address in props.remoteAddresses" :key="address" class="kv">
            <span class="k">{{ t("settings.lan_address") }}</span><span>http://{{ address }}:{{ props.settings.webPort }}/</span>
          </div>
        </div>
        <p class="callout callout-warning">{{ t("settings.remote_access.warning") }}</p>
      </section>
      <section class="settings-subsection mcp-settings">
        <div class="settings-list">
          <div class="switch-row settings-option switch-card">
            <div>
              <strong>{{ t("settings.enable_mcp_service") }}</strong><span class="muted">{{ t("settings.remote_access.mcp_listen_help") }}</span>
            </div>
            <NxpSwitch
              id="st-mcp-enabled"
              v-model="props.settings.mcpEnabled"
              :aria-label="t('settings.enable_mcp_service')"
              @change="props.onMcpChange"
            />
          </div>
        </div>
        <div
          class="form-grid settings-single-field"
          :data-help="t('settings.remote_access.mcp_endpoint_help', { port: Number(props.settings.mcpPort) || 58732 })"
        >
          <div class="field">
            <label class="field-label" for="st-mcp-port">{{ t("settings.mcp_port") }}</label>
            <NxpNumberInput
              id="st-mcp-port"
              v-model.number="props.settings.mcpPort"
              :min="1024"
              :max="65535"
              :aria-label="t('settings.mcp_port')"
              @change="props.saveWithRestart"
            />
          </div>
        </div>
      </section>
    </div>
  </NxpCollapsibleCard>
</template>
