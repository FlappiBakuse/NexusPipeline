<script setup lang="ts">
import { fieldHelp as vHelp } from '../components/field-help';
import { computed, ref } from 'vue';
import type { AutomationStore } from '../app/store-ports';
import type { AutomationBundle } from '../control/wire.generated';
import { desktop } from '../app/desktop';
import { usePathPicker } from './editor-path-picker';
import { useEditorNotifications } from './editor-notification-choices';
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
const failure = ref('');
const notifications = useEditorNotifications(props.store, reason => {
  failure.value = reason instanceof Error ? reason.message : '通知任务不可用';
});
const choosePath = usePathPicker();
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
  const id = props.id, selected = await choosePath('file');
  if (selected && props.id === id) path(kind, selected);
}
</script>

<template>
  <div class="binding-policy">
    <section data-surface="raised">
      <h3 :tabindex="locked('general') ? 0 : undefined"
        v-help="locked('general') ? t('使用全局设置，当前绑定的独立设置已保留。') : ''">{{ t('通用') }}</h3>
      <nexus-switch-list>
        <nexus-switch-setting
        data-draft-field="BindingPolicyFields/(groups.general as Overrides['general']).syncEnabled"
          v-if="global"
          :title="t('同步通用设置')"
          :description="t('开启后覆盖每个脚本绑定的通用设置')"
          :value.prop="(groups.general as Overrides['general']).syncEnabled"
          @nexus-change="set('general', 'syncEnabled', $event.detail.value)"
        />
        <nexus-switch-setting
        data-draft-field="BindingPolicyFields/groups.general.enabled"
          :title="t('是否启用')"
          :description="t('运行天数为 0 时不会参与运行')"
          :disabled.prop="locked('general')"
          :value.prop="groups.general.enabled"
          @nexus-change="set('general', 'enabled', $event.detail.value)"
        />
      </nexus-switch-list>
      <label v-help="t('-1 表示永久运行，0 表示停止，正数为剩余运行天数，每日递减。')">
        <span class="field-help-caption"><span>{{ t('运行天数') }}</span></span>
        <nexus-number-input
        data-draft-field="BindingPolicyFields/groups.general.runDays ?? -1"
          :placeholder="t('-1 永久；0 停止')"
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
      <label v-help="t('-1 表示不限制；正整数达到当天成功次数上限后跳过，0 无效。') +
        (global ? '' : '\n' + t('当天成功次数达到上限后，该绑定将跳过运行；失败、取消和已跳过不计入成功次数。'))">
        <span class="field-help-caption"><span>{{ t('最多成功运行次数') }}</span></span>
        <nexus-number-input
        data-draft-field="BindingPolicyFields/groups.general.maximumSuccessfulRunsPerDay ?? -1"
          :placeholder="t('-1 表示不限次数')"
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
    </section>
    <section data-surface="raised">
      <h3 :tabindex="locked('notification') ? 0 : undefined"
        v-help="locked('notification') ? t('使用全局设置，当前绑定的独立设置已保留。') : ''">{{ t('通知') }}</h3>
      <nexus-switch-list>
        <nexus-switch-setting
        data-draft-field="BindingPolicyFields/(groups.notification as Overrides['notification']).syncEnabled"
          v-if="global"
          :title="t('同步通知设置')"
          :description="t('开启后覆盖每个脚本绑定的通知设置')"
          :value.prop="(groups.notification as Overrides['notification']).syncEnabled"
          @nexus-change="set('notification', 'syncEnabled', $event.detail.value)"
        />
        <nexus-switch-setting
        data-draft-field="BindingPolicyFields/groups.notification.notifyEnabled"
          :title="t('开启通知推送')"
          :description="t('按用户绑定通知设置发送运行状态通知。')"
          :disabled.prop="locked('notification')"
          :value.prop="groups.notification.notifyEnabled"
          @nexus-change="set('notification', 'notifyEnabled', $event.detail.value)"
        />
      </nexus-switch-list>
      <label>
        {{ t('通知任务') }}
        <nexus-select
        data-draft-field="BindingPolicyFields/groups.notification.notificationTaskIds"
          :aria-label="t('通知任务')"
          :multiple.prop="true"
          :disabled.prop="locked('notification')"
          :value.prop="groups.notification.notificationTaskIds"
          :placeholder="t('不发送通知')"
          :options.prop="notifications.map((item) => ({ id: item.id, label: item.name }))"
          @nexus-change="set('notification', 'notificationTaskIds', $event.detail.value)"
        />
      </label>
      <nexus-toast v-if="failure" tone="danger" :message.prop="t(failure)" />
    </section>
    <section data-surface="raised">
      <h3 :tabindex="locked('advanced') ? 0 : undefined"
        v-help="locked('advanced') ? t('使用全局设置，当前绑定的独立设置已保留。') : ''">{{ t('高级') }}</h3>
      <nexus-switch-list v-if="global">
        <nexus-switch-setting
        data-draft-field="BindingPolicyFields/(groups.advanced as Overrides['advanced']).syncEnabled"
          :title="t('同步高级设置')"
          :description="t('开启后覆盖每个脚本绑定的高级设置')"
          :value.prop="(groups.advanced as Overrides['advanced']).syncEnabled"
          @nexus-change="set('advanced', 'syncEnabled', $event.detail.value)"
        />
      </nexus-switch-list>
      <label v-help="t('选择后仍可手动编辑；%FIRST% 前缀表示仅首次运行。')">
        <span class="field-help-caption"><span>{{ t('任务前运行脚本路径') }}</span></span>
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
      <label v-help="t('选择后仍可手动编辑；%LAST% 前缀表示仅最终运行。')">
        <span class="field-help-caption"><span>{{ t('任务后运行脚本路径') }}</span></span>
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
nexus-switch-list {
  margin-bottom: 12px;
}
nexus-number-input {
  width: 100%;
}
</style>
