<script setup lang="ts">
import { createIdentity } from '../control/identity';
import { computed, reactive, ref } from 'vue';
import type { AppStore } from '../app/store';
import type { InterfaceCategory } from '../control/interface';
import { integerValue, objectValue } from '../control/client';
import { validateForm } from '../components/form-validation';
import { useSettingsSecrets } from './settings-secret-state';
import { useSettingsSession } from './settings-session';
import { useSettingsView } from './settings-view-state';
import SettingsFields from './SettingsFields.vue';
import SettingsDiagnostics from './SettingsDiagnostics.vue';
import SettingsUpdates from './SettingsUpdates.vue';
import { t } from '../app/locale';
import './settings-messages';

const props = defineProps<{ store: AppStore; visible: boolean; wallpaperAvailable?: boolean }>();
const form = ref<HTMLElement>();
const notice = ref('');
const restartPort = ref<number | null>(null);
const categories: { id: InterfaceCategory; label: string; description: string }[] = [
  { id: 'service', label: '服务行为', description: '服务启动、历史记录与日志选项' },
  { id: 'remote', label: '远程访问和 MCP', description: '远程管理入口与本机 Agent 服务' },
  { id: 'proxy', label: '网络代理', description: '宿主外部 HTTP/HTTPS 请求' },
  { id: 'advanced', label: '高级设置', description: '控制需要手动确认的配置修复行为' },
  { id: 'updates', label: '更新设置', description: '更新渠道、检查与应用操作' },
  { id: 'diagnostics', label: '系统诊断', description: '检查运行环境、恢复现场与插件状态' }
];
const host = reactive(useSettingsSession(props.store, () => validateForm(form.value)));
const view = reactive(
  useSettingsView(
    props.store,
    categories.map((item) => item.id)
  )
);
const secrets = reactive(useSettingsSecrets(props, notice));
const panels = computed(() =>
  [...categories].sort(
    (left, right) => Number(view.pinned.includes(right.id)) - Number(view.pinned.includes(left.id))
  )
);
const canEdit = computed(() => props.store.can('settings.write') && !host.locked);
const unknownSecret = computed(() => !!secrets.tokenUnknownId || !!secrets.secretUnknownId);

async function setToken(value: string): Promise<void> {
  if (!value || !secrets.tokenReady || secrets.tokenUnknownId) return;
  secrets.token = value;
  secrets.tokenMode = 'set';
  await secrets.saveToken();
}
async function generateToken(): Promise<void> {
  if (!secrets.tokenReady || secrets.tokenUnknownId) return;
  secrets.generateToken();
  await secrets.saveToken();
}
async function clearToken(): Promise<void> {
  if (!secrets.tokenReady || secrets.tokenUnknownId) return;
  secrets.tokenMode = 'clear';
  await secrets.saveToken();
}
async function setPassword(value: string): Promise<void> {
  if (!value || !secrets.secretReady || secrets.secretUnknownId) return;
  secrets.secret = value;
  secrets.secretMode = 'set';
  await secrets.saveSecret();
}
async function restart(): Promise<void> {
  if (!props.store.can('maintenance.write')) return;
  try {
    const result = await props.store.client.restartHost(createIdentity(), objectValue);
    restartPort.value = integerValue(result.port);
    notice.value = '重启交接已受理。';
  } catch (reason) {
    props.store.report(reason);
    notice.value = '重启未确认，请检查当前服务状态。';
  }
}
</script>

<template>
  <main ref="form" class="settings-page">
    <p v-if="host.session.error" role="alert" class="error">
      {{ t(host.session.error) }}
      <button v-if="host.session.uncertain" :disabled="host.session.busy" @click="host.inspect">
        {{ t('查询原操作') }}
      </button>
      <button v-else-if="!host.conflict" :disabled="host.session.busy" @click="host.save">
        {{ t('重试保存') }}
      </button>
    </p>
    <p v-if="host.conflict && !host.session.pending" role="alert" class="notice">
      {{ t('设置已在其他位置更改，当前草稿已保留。') }}
      <button @click="host.loadLatest">{{ t('载入最新') }}</button>
    </p>
    <p v-if="view.state.error" role="alert" class="error">
      {{ t(view.state.error) }}
      <button v-if="view.state.pending" :disabled="view.state.busy" @click="view.checkPending">
        {{ t('查询原操作') }}
      </button>
      <button v-else-if="!view.conflict" :disabled="view.state.busy" @click="view.save">
        {{ t('重试保存') }}
      </button>
    </p>
    <p v-if="view.conflict && !view.state.pending" role="alert" class="notice">
      {{ t('视图设置已在其他位置更新。') }}
      <button @click="view.loadLatest">{{ t('载入最新') }}</button>
    </p>
    <p v-if="notice" role="status">{{ t(notice) }}{{ restartPort ? ' ' + restartPort : '' }}</p>
    <p v-if="store.state.settings?.restartReasons.length" class="notice">
      {{ t('更改等待重启：') }} {{ store.state.settings.restartReasons.join('、') }}
      <button :disabled="!store.can('maintenance.write')" @click="restart">{{ t('重启服务') }}</button>
    </p>
    <nav class="categories" :aria-label="t('设置类别')">
      <template v-for="item in categories" :key="item.id">
      <button v-if="item.id === 'diagnostics' && wallpaperAvailable" type="button"
        @click="form?.querySelector('#settings-extension-panels')?.scrollIntoView({ block: 'start' })">{{ t('自定义壁纸') }}</button>
      <button
        type="button"
        :aria-controls="'settings-' + item.id"
        :aria-expanded="view.isExpanded(item.id)"
        @click="view.choose(item.id)"
      >
        {{ t(item.label) }}
      </button>
      </template>
    </nav>
    <template v-for="item in panels" :key="item.id">
    <div v-if="item.id === 'diagnostics'" id="settings-extension-panels"><slot name="settings-panels" /></div>
    <section :id="'settings-' + item.id" class="panel">
      <button class="panel-heading" :aria-expanded="view.isExpanded(item.id)" @click="view.toggle(item.id)">
        <span>
          <strong>{{ t(item.label) }}</strong>
          <small>{{ t(item.description) }}</small>
        </span>
        <nexus-icon aria-hidden="true" :name="view.isExpanded(item.id) ? 'chevronDown' : 'chevronRight'" />
      </button>
      <div v-show="view.isExpanded(item.id)" class="panel-body" :inert="!view.isExpanded(item.id)">
        <SettingsDiagnostics
          v-if="item.id === 'diagnostics'"
          :store="store"
          :visible="visible && view.isExpanded(item.id)"
        />
        <SettingsFields
          v-else
          :category="item.id"
          :value="host.session.draft"
          :disabled="!canEdit"
          :locale="view.state.values.locale"
          :locale-disabled="view.state.busy || !!view.state.pending"
          @patch="host.patch"
          @locale="view.setLocale"
        >
          <template #token>
            <div v-if="store.can('secrets.status')" class="token">
              <label>
                {{ t('访问令牌') }}
                <div class="token-input">
                  <input
                    :value="secrets.tokenVisible ? secrets.token : ''"
                    :placeholder="t('留空不修改')"
                    :disabled="
                      !secrets.tokenReady ||
                      secrets.tokenBusy ||
                      !!secrets.tokenUnknownId ||
                      !store.can('secrets.write')
                    "
                    autocomplete="new-password"
                    @focus="secrets.revealToken"
                    @input="secrets.editToken(($event.target as HTMLInputElement).value)"
                    @change="setToken(($event.target as HTMLInputElement).value)"
                  />
                  <button
                    :disabled="
                      !secrets.tokenReady ||
                      secrets.tokenBusy ||
                      !!secrets.tokenUnknownId ||
                      !store.can('secrets.write')
                    "
                    @click="generateToken"
                  >
                    {{ t('生成令牌') }}
                  </button>
                  <button
                    class="danger"
                    :disabled="
                      !secrets.tokenReady ||
                      secrets.tokenBusy ||
                      !!secrets.tokenUnknownId ||
                      !store.can('secrets.write')
                    "
                    @click="clearToken"
                  >
                    {{ t('清空令牌') }}
                  </button>
                </div>
              </label>
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
              <p class="warning">
                {{
                  t(
                    '安全提示：启用远程访问后，持有令牌的人都能管理本机脚本与配置。请勿在公共网络环境开启。令牌与配置数据绑定当前电脑（DPAPI）加密，不可迁移。'
                  )
                }}
              </p>
              <p v-if="secrets.tokenError" role="alert" class="error">
                {{ t(secrets.tokenError) }}
                <button v-if="!secrets.tokenReady" @click="secrets.loadToken">{{ t('重试') }}</button>
              </p>
              <button
                v-if="secrets.tokenUnknownId"
                :disabled="secrets.tokenBusy"
                @click="secrets.inspectTokenOperation"
              >
                {{ t('查询令牌操作') }}
              </button>
              <p v-if="secrets.tokenOperationStatus" role="status">{{ t(secrets.tokenOperationStatus) }}</p>
            </div>
          </template>
          <template #proxy-password>
            <label v-if="store.can('secrets.status')">
              {{ t('密码') }}
              <input
                type="password"
                :value="secrets.secretVisible ? secrets.secret : ''"
                :placeholder="t('留空不修改')"
                :disabled="
                  !secrets.secretReady ||
                  secrets.secretBusy ||
                  !!secrets.secretUnknownId ||
                  !store.can('secrets.write')
                "
                autocomplete="new-password"
                @focus="secrets.revealSecret"
                @input="secrets.editSecret(($event.target as HTMLInputElement).value)"
                @change="setPassword(($event.target as HTMLInputElement).value)"
              />
              <span v-if="secrets.secretError" role="alert" class="error">
                {{ t(secrets.secretError) }}
                <button v-if="!secrets.secretReady" @click="secrets.loadSecret">{{ t('重试') }}</button>
              </span>
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
              :visible="visible && view.isExpanded(item.id)"
              :channel="host.session.draft.updates.channel"
            />
          </template>
        </SettingsFields>
      </div>
    </section>
    </template>
    <slot name="settings-extra" />
    <p v-if="unknownSecret" class="notice">{{ t('秘密变更结果待确认，原操作已锁定。') }}</p>
  </main>
</template>

<style scoped src="./settings-page.css"></style>
