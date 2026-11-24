<script setup lang="ts">
import { fieldHelp as vHelp } from '../components/field-help';
import { createIdentity } from '../control/identity';
import { computed, reactive, ref, watch } from 'vue';
import type { AppStore } from '../app/store';
import type { InterfaceCategory } from '../control/interface';
import { ControlFailure, integerValue, objectValue } from '../control/client';
import { validateForm } from '../components/form-validation';
import { useSettingsSecrets } from './settings-secret-state';
import { useSettingsSession } from './settings-session';
import { useSettingsView, useSettingsCategories } from './settings-view';
import SettingsFields from './SettingsFields.vue';
import SettingsDiagnostics from './SettingsDiagnostics.vue';
import SettingsUpdates from './SettingsUpdates.vue';
import { t } from '../app/locale';
import './settings-messages';

const props = defineProps<{ store: AppStore; visible: boolean }>();
const form = ref<HTMLElement>();
const notice = ref('');
const restartPort = ref<number | null>(null);
const restarting = ref(false);
const restartError = ref('');
const categories: { id: InterfaceCategory; label: string; description: string }[] = [
  { id: 'service', label: '服务行为', description: '服务启动、历史记录与日志选项' },
  { id: 'remote', label: '远程访问和 MCP', description: '远程管理入口与本机 Agent 服务' },
  { id: 'proxy', label: '网络代理', description: '宿主外部 HTTP/HTTPS 请求' },
  { id: 'advanced', label: '高级设置', description: '控制需要手动确认的配置修复行为' },
  { id: 'updates', label: '更新设置', description: '更新渠道、检查与应用操作' },
  { id: 'diagnostics', label: '系统诊断', description: '检查运行环境、恢复现场与插件状态' }
];
const host = reactive(useSettingsSession(props.store, () => validateForm(form.value)));
const categoryIds = reactive(categories.map(item => item.id));
const view = reactive(useSettingsView(props.store, categoryIds));
const categoryOwner = useSettingsCategories(form, categoryIds, view);
const extensionCategories = categoryOwner.extensions;
watch(() => [view.state.values.pinnedSettings, view.state.values.collapsedSettings], categoryOwner.synchronize, { flush: 'post' });
const secrets = reactive(useSettingsSecrets(props, notice));
const accessToken = computed({ get: () => secrets.token, set: secrets.editToken });
const proxyPassword = computed({ get: () => secrets.secret, set: secrets.editSecret });
const panels = computed(() =>
  [...categories].sort(
    (left, right) => Number(view.pinned.includes(right.id)) - Number(view.pinned.includes(left.id))
  )
);
const canEdit = computed(() => props.store.can('settings.write') && !host.locked);
const tokenDisabled = computed(() => !secrets.tokenReady || secrets.tokenBusy ||
  !!secrets.tokenUnknownId || !props.store.can('secrets.write'));

function preserveTokenFocus(event: PointerEvent): void {
  // An explicit pointer action takes precedence over submitting the field on blur.
  if (document.activeElement?.id === 'settings-access-token') event.preventDefault();
}
async function generateToken(): Promise<void> {
  if (tokenDisabled.value) return;
  secrets.generateToken();
  await secrets.saveToken();
}
async function clearToken(): Promise<void> {
  if (tokenDisabled.value) return;
  await secrets.clearToken();
}
async function setPassword(value: string): Promise<void> {
  if (!value || !secrets.secretReady || secrets.secretUnknownId) return;
  secrets.secret = value;
  secrets.secretMode = 'set';
  await secrets.saveSecret();
}
async function restart(): Promise<void> {
  if (restarting.value || !props.store.can('maintenance.write')) return;
  restarting.value = true;
  restartError.value = '';
  try {
    const result = await props.store.client.restartHost(createIdentity(), objectValue);
    restartPort.value = integerValue(result.port);
    notice.value = '重启交接已受理。';
  } catch (reason) {
    restartError.value = reason instanceof ControlFailure && !reason.uncertain
      ? reason.code : '重启未确认，请检查当前服务状态。';
  } finally {
    restarting.value = false;
  }
}
</script>

<template>
  <main ref="form" class="settings-page" @nexus-toggle="categoryOwner.toggle">
    <template v-if="visible">
      <nexus-toast v-if="host.session.error" tone="danger" :message.prop="t(host.session.error)" />
      <nexus-toast v-if="view.state.error" tone="danger" :message.prop="t(view.state.error)" />
      <nexus-toast v-if="restartError" tone="danger" :message.prop="t(restartError)" @nexus-close="restartError = ''" />
      <nexus-toast v-else-if="notice" :message.prop="t(notice) + (restartPort ? ' ' + restartPort : '')" @nexus-close="notice = ''" />
    </template>
    <section v-if="store.state.settings?.restartReasons.length" class="warning restart-notice">
      <p role="status">{{ t('有改动需要重启服务后生效。') }}</p>
      <button type="button" class="primary" :disabled="restarting || !store.can('maintenance.write')"
        @click="restart">{{ t('重启服务') }}</button>
    </section>
    <nav class="categories" :aria-label="t('设置类别')">
      <template v-for="item in categories" :key="item.id">
      <button
        type="button"
        :aria-controls="'settings-' + item.id"
        :aria-expanded="view.isExpanded(item.id)"
        @click="categoryOwner.choose(item.id)"
      >
        {{ t(item.label) }}
      </button>
      </template>
      <button v-for="item in extensionCategories" :key="item.id" type="button" :aria-expanded="view.isExpanded(item.id)" @click="categoryOwner.choose(item.id)">{{ item.label }}</button>
    </nav>
    <div id="settings-extension-panels"><slot name="settings-panels" /></div>
    <template v-for="item in panels" :key="item.id">
    <section :id="'settings-' + item.id" class="panel" data-surface="collapsible" :style="{ order: categoryOwner.order(item.id) }">
      <button class="panel-heading" :aria-expanded="categoryOwner.isExpanded(item.id)" @click="categoryOwner.toggleCard(item.id)">
        <span>
          <strong>{{ t(item.label) }}</strong>
          <small>{{ t(item.description) }}</small>
        </span>
        <nexus-icon class="disclosure-chevron" aria-hidden="true" :name="categoryOwner.isExpanded(item.id) ? 'chevronDown' : 'chevronRight'" />
      </button>
      <div class="disclosure-reveal" :data-expanded="categoryOwner.isExpanded(item.id)" :inert="!categoryOwner.isExpanded(item.id)"><div class="disclosure-clip"><div class="panel-body">
        <SettingsDiagnostics
          v-if="item.id === 'diagnostics'"
          :store="store"
          :visible="visible && categoryOwner.isExpanded(item.id)"
        />
        <SettingsFields
          v-else
          :key="String(visible) + '/' + String(categoryOwner.isExpanded(item.id))"
          :category="item.id"
          :value="host.session.values"
          :disabled="!canEdit"
          :locale="view.state.values.locale"
          :locale-disabled="view.state.unknown"
          @patch="host.patch"
          @locale="view.setLocale"
        >
          <template #token>
            <div v-if="store.can('secrets.status')" class="token">
              <div class="token-field" v-help="t('自定义令牌支持 4–256 个英文字母、数字、短横线或下划线，移开焦点即保存。留空保持当前令牌。') + '\n' + t('安全提示：启用远程访问后，持有令牌的人都能管理本机脚本与配置。请勿在公共网络环境开启。令牌与配置数据绑定当前电脑（DPAPI）加密，不可迁移。')">
                <span class="field-help-caption"><label for="settings-access-token">{{ t('访问令牌') }}</label></span>
                <div class="token-input">
                  <input
                    id="settings-access-token"
                    v-model="accessToken"
                    :type="secrets.tokenVisible ? 'text' : 'password'"
                    :placeholder="t('留空不修改')"
                    :disabled="tokenDisabled"
                    autocomplete="new-password"
                    autocorrect="off"
                    autocapitalize="none"
                    spellcheck="false"
                    @focus="secrets.revealToken"
                    @blur="secrets.blurToken"
                  />
                  <button type="button" :disabled="tokenDisabled" @pointerdown="preserveTokenFocus" @click="generateToken">
                    {{ t('生成令牌') }}
                  </button>
                  <button type="button" class="danger" :disabled="tokenDisabled" @pointerdown="preserveTokenFocus" @click="clearToken">
                    {{ t('清空令牌') }}
                  </button>
                </div>
              </div>
              <dl class="addresses">
                <div>
                  <dt>{{ t('内网访问地址') }}</dt>
                  <dd>
                    {{ store.state.status?.accessAddresses?.lan.join('、') || t('未检测到可用内网地址') }}
                  </dd>
                </div>
                <div>
                  <dt>{{ t('公网访问地址') }}</dt>
                  <dd>
                    {{
                      store.state.status?.accessAddresses?.public.join('、') ||
                      t('未检测到公网网卡地址；NAT 环境需配置公网域名与端口映射')
                    }}
                  </dd>
                </div>
              </dl>

              <nexus-toast v-if="visible && secrets.tokenError" tone="danger" :message.prop="t(secrets.tokenError)" />
              <button v-if="secrets.tokenError && !secrets.tokenReady" type="button" @click="secrets.loadToken">{{ t('重试') }}</button>
              <button
                v-if="secrets.tokenUnknownId"
                :disabled="secrets.tokenBusy"
                @click="secrets.inspectTokenOperation"
              >
                {{ t('查询令牌操作') }}
              </button>
              <nexus-toast v-if="visible && !secrets.tokenError && secrets.tokenOperationStatus" tone="info" :message.prop="t(secrets.tokenOperationStatus)" />
            </div>
          </template>
          <template #proxy-password>
            <label v-if="store.can('secrets.status')" v-help="t('留空保持已保存的代理密码；输入新值后提交替换。')">
              <span class="field-help-caption"><span>{{ t('密码') }}</span></span>
              <input
                type="password"
                v-model="proxyPassword"
                :placeholder="t('留空不修改')"
                :disabled="
                  !secrets.secretReady ||
                  secrets.secretBusy ||
                  !!secrets.secretUnknownId ||
                  !store.can('secrets.write')
                "
                autocomplete="new-password"
                @focus="secrets.revealSecret"
                @change="setPassword(($event.target as HTMLInputElement).value)"
              />
              <nexus-toast v-if="visible && secrets.secretError" tone="danger" :message.prop="t(secrets.secretError)" />
              <button v-if="secrets.secretError && !secrets.secretReady" type="button" @click="secrets.loadSecret">{{ t('重试') }}</button>
              <button
                v-if="secrets.secretUnknownId"
                :disabled="secrets.secretBusy"
                @click="secrets.inspectSecretOperation"
              >
                {{ t('查询密码原操作') }}
              </button>
            </label>
          </template>
          <template #updates>
            <SettingsUpdates
              :store="store"
              :visible="visible && categoryOwner.isExpanded(item.id)"
              :channel="host.session.values.updates.channel"
            />
          </template>
        </SettingsFields>
      </div></div></div>
    </section>
    </template>
    <slot name="settings-extra" />
  </main>
</template>

<style scoped>
.settings-page {
  display: grid;
  gap: 16px;
  min-width: 0;
}
.categories {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  padding-bottom: 8px;
}
.categories button[aria-expanded='true'] {
  color: var(--accent);
  border-color: var(--accent);
  background: var(--content-control);
}
.panel {
  border: 1px solid var(--border);
  border-radius: 16px;
  overflow: hidden;
}
.panel-heading {
  display: flex;
  align-items: center;
  justify-content: space-between;
  width: 100%;
  min-height: 76px;
  padding: 16px 24px;
  text-align: left;
  border: 0;
  border-radius: 0;
  background: transparent;
}
.panel-heading > span {
  display: grid;
  gap: 4px;
}
.panel-heading strong {
  font-size: 15px;
}
.panel-heading small {
  color: var(--muted);
  font-size: 12px;
  font-weight: 600;
}
.panel-heading:hover { background: transparent; }
.panel-body {
  border-top: 1px solid var(--border);
  padding: 24px;
}
.token {
  display: grid;
  gap: 16px;
}
label,
.token-field {
  display: grid;
  gap: 6px;
  color: var(--muted);
  font-size: 12px;
  font-weight: 600;
}
.token-input {
  display: flex;
  gap: 8px;
}
.token-input input {
  min-width: 0;
  flex: 1;
}
.token-input button {
  flex-shrink: 0;
}
input {
  min-height: 40px;
  box-sizing: border-box;
  font-weight: 400;
}
dl,
dd,
p {
  margin: 0;
}
.addresses > div {
  display: flex;
  gap: 12px;
  align-items: center;
  min-height: 34px;
  border-bottom: 1px solid var(--border);
}
dt {
  width: 94px;
  color: var(--muted);
}
.warning {
  padding: 12px 16px;
  border: 1px solid var(--border);
  border-radius: 8px;
  background: var(--content-card-soft);
}
.warning {
  border-left: 2px solid var(--warning);
  color: var(--muted);
  font-size: 12px;
  line-height: 1.7;
}
.restart-notice {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
}
.restart-notice p { flex: 1 1 240px; }
.error {
  color: var(--nx-color-danger);
}
@media (max-width: 600px) {
  .panel-heading,
  .panel-body {
    padding: 16px;
  }
  .token-input {
    flex-wrap: wrap;
  }
  .token-input input {
    flex-basis: 100%;
  }
  .addresses > div {
    align-items: flex-start;
    padding: 8px 0;
  }
}

#settings-extension-panels,
#settings-extension-panels :deep(.surface-outlet),
#settings-extension-panels :deep(.surface-outlet-content),
#settings-extension-panels :deep(.plugin-panel) { display: contents; }
</style>
