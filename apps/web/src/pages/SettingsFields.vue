<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import type { ControlHostSettingsValues } from '../control/wire.generated';
import type { InterfaceValues } from '../control/interface';
import { t } from '../app/locale';

const props = defineProps<{
  category: string;
  value: ControlHostSettingsValues;
  disabled: boolean;
  locale: InterfaceValues['locale'];
  localeDisabled: boolean;
}>();
const emit = defineEmits<{
  patch: [value: Partial<ControlHostSettingsValues>];
  locale: [value: InterfaceValues['locale']];
}>();
type Values = ControlHostSettingsValues;
function service(value: Partial<Values['service']>): void {
  emit('patch', { service: { ...props.value.service, ...value } });
}
function desktop(value: Partial<Values['desktop']>): void {
  emit('patch', { desktop: { ...props.value.desktop, ...value } });
}
const proxyInput = ref<Values['proxy']>();
const proxyValue = computed(() => proxyInput.value || props.value.proxy);
watch(() => props.value.proxy, () => { proxyInput.value = undefined; });
function proxy(value: Partial<Values['proxy']>): void {
  const next = { ...proxyValue.value, ...value };
  proxyInput.value = next;
  if (value.mode && value.mode !== 'http') next.url = props.value.proxy.url;
  if (next.mode === 'http') {
    try {
      const address = new URL(next.url || '');
      if (!['http:', 'https:'].includes(address.protocol) || !address.hostname) return;
    } catch { return; }
  }
  emit('patch', { proxy: next });
}
function updates(value: Partial<Values['updates']>): void {
  emit('patch', { updates: { ...props.value.updates, ...value } });
}
</script>

<template>
  <div class="settings-fields">
    <template v-if="category === 'service'">
      <div class="switches">
        <nexus-switch-setting
          :title="t('开机自启')"
          :description="t('注册到当前用户启动项')"
          :value.prop="value.autoStart"
          :disabled.prop="disabled"
          @nexus-change="emit('patch', { autoStart: $event.detail.value })"
        />
        <nexus-switch-setting
          :title="t('登录自启动时显示客户端')"
          :description="
            t(
              '登录自启动时显示桌面客户端；轻量模式下也决定首次启动是否打开网页。关闭后仍可双击或通过托盘打开。'
            )
          "
          :value.prop="value.desktop.openOnStartup"
          :disabled.prop="disabled"
          @nexus-change="desktop({ openOnStartup: $event.detail.value })"
        />
      </div>
      <div class="columns three">
        <label>
          {{ t('历史保留天数') }}
          <FormNumber
            :disabled="disabled"
            :model-value="value.historyRetentionDays"
            :min="1"
            :max="180"
            @update:model-value="$event !== null && emit('patch', { historyRetentionDays: $event })"
          />
        </label>
        <label>
          {{ t('Web 端口') }}
          <FormNumber
            :disabled="disabled"
            :model-value="value.service.webPort"
            :min="0"
            :max="65535"
            :validate="(port) => port === 0 || (port !== null && port >= 1024)"
            @update:model-value="$event !== null && service({ webPort: $event })"
          />
        </label>
        <label>
          {{ t('日志级别') }}
          <FormSelect
            :disabled="disabled"
            :model-value="value.logLevel"
            @update:model-value="emit('patch', { logLevel: $event })"
          >
            <option value="debug">{{ t('调试') }}</option>
            <option value="info">{{ t('信息') }}</option>
            <option value="warning">{{ t('警告') }}</option>
            <option value="error">{{ t('错误') }}</option>
            <option value="fatal">{{ t('致命') }}</option>
          </FormSelect>
        </label>
      </div>
      <div class="columns">
        <label>
          {{ t('界面语言') }}
          <FormSelect
            :model-value="locale"
            :disabled="disabled || localeDisabled"
            @update:model-value="emit('locale', $event)"
          >
            <option value="zh-CN">简体中文</option>
            <option value="en-US">English</option>
          </FormSelect>
        </label>
        <label>
          {{ t('宿主语言') }}
          <FormSelect
            :disabled="disabled"
            :model-value="value.locale"
            @update:model-value="emit('patch', { locale: $event })"
          >
            <option value="zhCn">简体中文</option>
            <option value="enUs">English</option>
          </FormSelect>
        </label>
      </div>
    </template>
    <template v-else-if="category === 'remote'">
      <div class="switches">
        <nexus-switch-setting
          :title="t('远程访问')"
          :description="t('绑定所有网卡，API 需要访问令牌；本地 127.0.0.1 请求豁免')"
          :value.prop="value.service.allowRemoteAccess"
          :disabled.prop="disabled"
          @nexus-change="service({ allowRemoteAccess: $event.detail.value })"
        />
      </div>
      <slot name="token" />
      <div class="divider" />
      <div class="switches">
        <nexus-switch-setting
          :title="t('启用 MCP 服务')"
          :description="t('重启后监听本机 MCP 端点')"
          :value.prop="value.service.mcpEnabled"
          :disabled.prop="disabled"
          @nexus-change="service({ mcpEnabled: $event.detail.value })"
        />
        <nexus-switch-setting
          :title="t('允许 MCP 写入与执行')"
          :description="t('默认关闭。开启后允许修改配置、运行任务和发送测试通知。')"
          :value.prop="value.service.mcpWriteEnabled"
          :disabled.prop="disabled"
          @nexus-change="service({ mcpWriteEnabled: $event.detail.value })"
        />
      </div>
      <label>
        {{ t('MCP 端口') }}
        <FormNumber
          :disabled="disabled"
          :model-value="value.service.mcpPort"
          :min="1024"
          :max="65535"
          @update:model-value="$event !== null && service({ mcpPort: $event })"
        />
      </label>
    </template>
    <template v-else-if="category === 'proxy'">
      <label>
        {{ t('代理模式') }}
        <FormSelect
          :disabled="disabled"
          :model-value="proxyValue.mode"
          @update:model-value="proxy({ mode: $event })"
        >
          <option value="none">{{ t('不使用代理') }}</option>
          <option value="system">{{ t('系统代理') }}</option>
          <option value="http">{{ t('HTTP 代理') }}</option>
        </FormSelect>
      </label>
      <template v-if="proxyValue.mode === 'http'">
        <label>
          {{ t('代理地址') }}
          <input
            :disabled="disabled"
            :value="proxyValue.url || ''"
            type="url"
            required
            pattern="https?://.+"
            placeholder="http://127.0.0.1:7890"
            @change="proxy({ url: ($event.target as HTMLInputElement).value || null })"
          />
        </label>
        <div class="proxy-credentials">
          <label>
            {{ t('用户名') }}
            <input
              :disabled="disabled"
              :value="proxyValue.userName"
              autocomplete="off"
              @change="proxy({ userName: ($event.target as HTMLInputElement).value })"
            />
          </label>
          <slot name="proxy-password" />
        </div>
      </template>
    </template>
    <div v-else-if="category === 'advanced'" class="switches">
      <nexus-switch-setting
        :title="t('逐次配置修复')"
        :description="t('允许在每次运行前检查并修复配置；关闭时保留原配置。')"
        :value.prop="value.allowConfigRepair"
        :disabled.prop="disabled"
        @nexus-change="emit('patch', { allowConfigRepair: $event.detail.value })"
      />
    </div>
    <template v-else-if="category === 'updates'">
      <div class="switches">
        <nexus-switch-setting
          :title="t('定期检查更新')"
          :description="t('服务启动约 5 秒后首次检查，此后每次自动检查完成后间隔 12 小时')"
          :value.prop="value.updates.checkEnabled"
          :disabled.prop="disabled"
          @nexus-change="updates({ checkEnabled: $event.detail.value })"
        />
        <nexus-switch-setting
          :title="t('闲时自动更新')"
          :description="t('发现新版本后自动下载；运行期间在安全闲时应用，启动时先更新再启动。')"
          :value.prop="value.updates.autoApplyEnabled"
          :disabled.prop="disabled"
          @nexus-change="updates({ autoApplyEnabled: $event.detail.value })"
        />
        <nexus-switch-setting
          :title="t('插件自动更新')"
          :description="t('启动时检查并下载；运行期间定期下载，闲时安全重启应用。')"
          :value.prop="value.updates.pluginAutoUpdateEnabled"
          :disabled.prop="disabled"
          @nexus-change="updates({ pluginAutoUpdateEnabled: $event.detail.value })"
        />
      </div>
      <div class="columns">
        <label>
          {{ t('更新渠道') }}
          <FormSelect
            :disabled="disabled"
            :model-value="value.updates.channel"
            @update:model-value="updates({ channel: $event })"
          >
            <option value="stable">{{ t('稳定版 (Stable)') }}</option>
            <option value="prerelease">{{ t('预发布 (Pre-release)') }}</option>
          </FormSelect>
        </label>
        <label>
          {{ t('镜像源地址') }}
          <input
            :disabled="disabled"
            :value="value.updates.sourceUrl || ''"
            :placeholder="t('默认 GitHub')"
            @change="updates({ sourceUrl: ($event.target as HTMLInputElement).value || null })"
          />
        </label>
      </div>
      <slot name="updates" />
    </template>
  </div>
</template>

<style scoped src="./settings-fields.css"></style>
