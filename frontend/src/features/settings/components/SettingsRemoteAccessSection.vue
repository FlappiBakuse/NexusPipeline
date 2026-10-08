<script setup lang="ts">
import { t } from "../../../platform/i18n";
import NxpCollapsibleCard from "../../../ui/composites/NxpCollapsibleCard.vue";
import NxpSwitchSetting from "../../../ui/composites/NxpSwitchSetting.vue";
import NxpButton from "../../../ui/primitives/NxpButton.vue";
import NxpNumberInput from "../../../ui/primitives/NxpNumberInput.vue";
import NxpTextInput from "../../../ui/primitives/NxpTextInput.vue";
import type { Settings } from "../utils/settingsTypes";

const props = defineProps<{
  settings: Settings;
  expanded: boolean;
  token: string;
  tokenBusy: boolean;
  tokenError: string;
  remoteAddresses: { internalAddress: string | null; publicAddress: string | null; port: number };
  saveWithRestart: () => void;
  onRemoteChange: (value: boolean) => void;
  onMcpChange: (value: boolean) => void;
  generateToken: () => void;
  clearToken: () => void;
  readToken: () => void;
}>();
const emit = defineEmits<{ toggle: []; "update:token": [value: string] }>();
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
          <NxpSwitchSetting
            id="st-remote"
            :model-value="props.settings.allowRemoteAccess === true"
            :label="t('settings.remote_access')"
            :description="t('settings.remote_access.bind_all_help')"
            :aria-label="t('settings.remote_access')"
            @update:model-value="props.onRemoteChange"
          />
        </div>
        <div class="field-btn-row">
          <div class="field" :data-help="t('settings.remote_access.token_keep_help')">
            <label class="field-label" for="st-token">{{ t("settings.access_token") }}</label>
            <NxpTextInput
              id="st-token"
              :model-value="props.token"
              type="password"
              show-password-toggle
              :show-password-label="t('common.show')"
              :hide-password-label="t('settings.hide')"
              :disabled="props.tokenBusy"
              :aria-label="t('settings.access_token')"
              autocomplete="new-password"
              :placeholder="t('common.leave_blank_to_keep')"
              @update:model-value="emit('update:token', $event)"
            />
          </div>
          <NxpButton class="ghost" type="button" data-testid="gen-token" :disabled="props.tokenBusy" @click="props.generateToken">
            {{ t("settings.generate_token") }}
          </NxpButton>
          <NxpButton type="button" tone="danger" :disabled="props.tokenBusy" @click="props.clearToken">{{ t('settings.remote_access.clear_token') }}</NxpButton>
        </div>
        <p v-if="props.tokenError" class="callout callout-warning" role="alert">{{ props.tokenError }}</p>
        <NxpButton v-if="props.tokenError" type="button" @click="props.readToken">{{ t('settings.remote_access.read_retry') }}</NxpButton>
        <div
          id="remote-lan-list"
          class="detail"
          :data-help="props.settings.allowRemoteAccess ? t('settings.remote_access.lan_help_short') : undefined"
        >
          <div class="kv">
            <span class="k">{{ t("settings.remote_access.internal_address") }}</span><span>{{ props.remoteAddresses.internalAddress ? `http://${props.remoteAddresses.internalAddress}:${props.remoteAddresses.port}/` : t('settings.remote_access.no_internal_address') }}</span>
          </div>
          <div class="kv">
            <span class="k">{{ t("settings.remote_access.public_address") }}</span><span>{{ props.remoteAddresses.publicAddress ? `http://${props.remoteAddresses.publicAddress}:${props.remoteAddresses.port}/` : t('settings.remote_access.no_public_address') }}</span>
          </div>
        </div>
        <p class="callout callout-warning">{{ t("settings.remote_access.warning") }}</p>
      </section>
      <section class="settings-subsection mcp-settings">
        <div class="settings-list">
          <NxpSwitchSetting
            id="st-mcp-enabled"
            v-model="props.settings.mcpEnabled"
            :label="t('settings.enable_mcp_service')"
            :description="t('settings.remote_access.mcp_listen_help')"
            :aria-label="t('settings.enable_mcp_service')"
            @change="props.onMcpChange"
          />
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
