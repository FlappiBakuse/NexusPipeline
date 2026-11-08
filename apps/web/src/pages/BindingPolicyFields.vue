<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import type { AutomationStore } from '../app/store-ports';
import type { AutomationBundle } from '../control/wire.generated';
import { desktop } from '../app/desktop';
import { notificationCatalog, type NotificationTask } from './notification-types';
import { hookPaths } from './hook-paths';
import { t } from '../app/locale';

type Binding = AutomationBundle['bindings'][number];
type Overrides = NonNullable<AutomationBundle['users'][number]['bindingOverrides']>;
const props = defineProps<{
  store: AutomationStore;
  id: string;
  modelValue: Binding | Overrides;
  global?: boolean;
  overrides?: Overrides;
  userId?: string;
}>();
const emit = defineEmits<{ 'update:modelValue': [value: Binding | Overrides] }>();
const notifications = ref<NotificationTask[]>([]);
const failure = ref('');
const owner = new AbortController();
const locked = (group: keyof Overrides) => !props.global && props.overrides?.[group]?.syncEnabled === true;
const groups = computed(() => props.global ? props.modelValue as Overrides : {
  general: locked('general') ? props.overrides!.general : props.modelValue as Binding,
  notification: locked('notification') ? props.overrides!.notification : props.modelValue as Binding,
  advanced: locked('advanced') ? props.overrides!.advanced : props.modelValue as Binding
});
const paths = computed(() => hookPaths(props.store, locked('advanced') ? props.userId! : props.id, groups.value.advanced));
function set(group: keyof Overrides, key: string, value: unknown): void {
  if (locked(group)) return;
  emit(
    'update:modelValue',
    props.global
      ? ({ ...props.modelValue, [group]: { ...groups.value[group], [key]: value } } as Overrides)
      : ({ ...props.modelValue, [key]: value } as Binding)
  );
}
function path(kind: 'pre' | 'post', value: string): void {
  if (locked('advanced')) return;
  paths.value[kind] = value;
  set('advanced', kind === 'pre' ? 'preOnceAfterSuccess' : 'postFinalOnly', /^%(?:FIRST|LAST)%/i.test(value));
}
async function browse(kind: 'pre' | 'post'): Promise<void> {
  if (locked('advanced')) return;
  const result = await desktop?.chooseFile();
  if (result && !result.canceled && result.paths[0]) path(kind, result.paths[0]);
}
onMounted(async () => {
  if (!props.store.can('notifications.read')) return;
  try {
    notifications.value = (
      await props.store.client.read('/control/v1/notifications', notificationCatalog, owner.signal)
    ).tasks;
  } catch (reason) {
    if (!owner.signal.aborted) failure.value = reason instanceof Error ? reason.message : '通知任务不可用';
  }
});
onBeforeUnmount(() => owner.abort());
</script>

<template>
  <div class="binding-policy">
    <section>
      <h3>{{ t('通用') }}</h3>
      <p v-if="locked('general')">{{ t('使用全局设置，当前绑定的独立设置已保留。') }}</p>
      <p>
        {{
          t(global ? '统一控制所有脚本绑定的启用状态与运行天数。' : '控制当前脚本绑定的启用状态与运行天数。')
        }}
      </p>
      <div class="switches">
        <nexus-switch-setting
          v-if="global"
          :title="t('同步通用设置')"
          :description="t('开启后覆盖每个脚本绑定的通用设置')"
          :value.prop="(groups.general as Overrides['general']).syncEnabled"
          @nexus-change="set('general', 'syncEnabled', $event.detail.value)"
        />
        <nexus-switch-setting
          :title="t('是否启用')"
          :description="t('运行天数为 0 时不会参与运行')"
          :disabled.prop="locked('general')"
          :value.prop="groups.general.enabled"
          @nexus-change="set('general', 'enabled', $event.detail.value)"
        />
      </div>
      <label>
        {{ t('运行天数') }}
        <nexus-number-input
          :aria-label="t('运行天数')"
          aria-required="true"
          :min.prop="-1"
          :max.prop="36500"
          :read-only.prop.camel="locked('general')"
          :inert="locked('general')"
          :value.prop="groups.general.runDays ?? -1"
          @nexus-change="set('general', 'runDays', $event.detail.value)"
        />
      </label>
      <label>
        {{ t('最多成功运行次数') }}
        <nexus-number-input
          :aria-label="t('最多成功运行次数')"
          aria-required="true"
          :min.prop="-1"
          :max.prop="9999"
          :aria-invalid="groups.general.maximumSuccessfulRunsPerDay === 0"
          :read-only.prop.camel="locked('general')"
          :inert="locked('general')"
          :value.prop="groups.general.maximumSuccessfulRunsPerDay ?? -1"
          @nexus-change="set('general', 'maximumSuccessfulRunsPerDay', $event.detail.value)"
        />
      </label>
      <p v-if="!global">
        {{
          t(
            '当天成功次数达到上限后，该绑定将跳过运行；失败、取消和已跳过不计入成功次数。'
          )
        }}
      </p>
    </section>
    <section>
      <h3>{{ t('通知') }}</h3>
      <p v-if="locked('notification')">{{ t('使用全局设置，当前绑定的独立设置已保留。') }}</p>
      <p>
        {{
          t(global ? '统一控制所有脚本绑定的通知开关与 SMTP 收件人。' : '用户绑定允许时发送运行结果通知。')
        }}
      </p>
      <div class="switches">
        <nexus-switch-setting
          v-if="global"
          :title="t('同步通知设置')"
          :description="t('开启后覆盖每个脚本绑定的通知设置')"
          :value.prop="(groups.notification as Overrides['notification']).syncEnabled"
          @nexus-change="set('notification', 'syncEnabled', $event.detail.value)"
        />
        <nexus-switch-setting
          :title="t('开启通知推送')"
          :description="t('按用户绑定通知设置发送运行状态通知。')"
          :disabled.prop="locked('notification')"
          :value.prop="groups.notification.notifyEnabled"
          @nexus-change="set('notification', 'notifyEnabled', $event.detail.value)"
        />
      </div>
      <label>
        {{ t('通知任务') }}
        <nexus-select
          :aria-label="t('通知任务')"
          :multiple.prop="true"
          :disabled.prop="locked('notification')"
          :value.prop="groups.notification.notificationTaskIds"
          :placeholder="t('不发送通知')"
          :options.prop="notifications.map((item) => ({ id: item.id, label: item.name }))"
          @nexus-change="set('notification', 'notificationTaskIds', $event.detail.value)"
        />
      </label>
      <p v-if="failure" role="alert">{{ failure }}</p>
    </section>
    <section>
      <h3>{{ t('高级') }}</h3>
      <p v-if="locked('advanced')">{{ t('使用全局设置，当前绑定的独立设置已保留。') }}</p>
      <p>{{ t('任务前后脚本设置。') }}</p>
      <div v-if="global" class="switches">
        <nexus-switch-setting
          :title="t('同步高级设置')"
          :description="t('开启后覆盖每个脚本绑定的高级设置')"
          :value.prop="(groups.advanced as Overrides['advanced']).syncEnabled"
          @nexus-change="set('advanced', 'syncEnabled', $event.detail.value)"
        />
      </div>
      <label>
        {{ t('任务前运行脚本路径') }}
        <nexus-path-picker
          :aria-label="t('任务前运行脚本路径')"
          :disabled.prop="locked('advanced')"
          :value.prop="paths.pre"
          :kinds.prop="['file']"
          :placeholder="t('%FIRST% 开头填写仅首次运行')"
          :browse-allowed.prop.camel="!!desktop"
          @nexus-change="path('pre', $event.detail.value)"
          @nexus-browse="browse('pre')"
        />
      </label>
      <label>
        {{ t('任务后运行脚本路径') }}
        <nexus-path-picker
          :aria-label="t('任务后运行脚本路径')"
          :disabled.prop="locked('advanced')"
          :value.prop="paths.post"
          :kinds.prop="['file']"
          :placeholder="t('%LAST% 开头填写仅最终运行')"
          :browse-allowed.prop.camel="!!desktop"
          @nexus-change="path('post', $event.detail.value)"
          @nexus-browse="browse('post')"
        />
      </label>
    </section>
  </div>
</template>

<style scoped>
.binding-policy {
  display: grid;
  gap: 16px;
}
section {
  display: grid;
  gap: 12px;
  padding: 16px;
  border: 1px solid var(--border);
  border-radius: 10px;
  background: var(--content-card);
}
h3 {
  margin: 0;
  font-size: 14px;
}
p {
  margin: -8px 0 0;
  font-size: 12px;
  color: var(--muted);
}
label {
  display: grid;
  gap: 7px;
  font-size: 12px;
  color: var(--muted);
}
.switches {
  display: grid;
  border: 1px solid var(--border);
  border-radius: 16px;
  overflow: hidden;
  background: var(--content-control);
  margin-bottom: 12px;
}
.switches > nexus-switch-setting + nexus-switch-setting {
  border-top: 1px solid var(--border);
}
nexus-number-input {
  width: 100%;
}
</style>
