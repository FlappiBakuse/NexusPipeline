<script setup lang="ts">
import { computed, reactive, watch } from 'vue';
import { arrayValue, ControlFailure, integerValue, objectValue, stringValue } from '../control/client';
import { createIdentity } from '../control/identity';
import type { AutomationStore } from '../app/store-ports';
import { registerMessages, t } from '../app/locale';
import { configurationDrafts } from './configuration-drafts';
import ExtensionOutlet, { useSurfaceAvailability } from '../extensions/ExtensionOutlet.vue';

registerMessages({
  打开编辑器: 'Open editor',
  '请选择要绑定的配置。': 'Choose the configuration to bind.',
  '正在打开脚本程序…': 'Opening the script program…',
  '主程序已启动。请在脚本程序中完成修改，回到这里保存为快照。取消将恢复原配置。':
    'The main program has started. Finish your changes there, then return here to save a snapshot. Cancel to restore the original configuration.',
  '点击「继续编辑」重新打开脚本程序，或取消本次修改。':
    'Select Continue editing to reopen the script program, or cancel this edit.',
  '继续编辑': 'Continue editing',
  '保存并绑定此配置': 'Save and bind this configuration',
  '将绑定更换为以下配置。请确认保存。': 'The binding will switch to the configuration below. Confirm to save.',
  '只能保留一个配置。请移除多余配置后再保存。': 'Only one configuration can be kept. Remove the extra configurations before saving.',
  '此配置的启动名称与安装目录中的另一套配置重复。请更改名称后再保存。': 'This configuration has the same launch name as another installed configuration. Change its name before saving.',
  '请保留一个配置后再保存。': 'Keep one configuration before saving.',
  '请先在脚本程序中创建一套配置，再重新打开编辑。': 'Create a configuration in the script program and open the editor again.',
  '请检查脚本目录与上游程序是否完整。': 'Check the script directory and installed program.',
  '请在 Host 本机打开脚本程序。': 'Open the script program on the Host machine.',
  '脚本实例配置丢失。请在脚本程序中重新创建配置，或导入可用的备份。':
    'This script instance lost its configuration. Recreate it in the script program or import an available backup.',
  '此脚本实例的配置还在恢复。请在设置中的诊断查看原因。':
    'This script instance still has configuration recovery pending. See diagnostics in Settings.',
  '保留到草稿': 'Keep in draft',
  '已保留到草稿，请保存账号。': 'Kept in the draft. Save the account to apply it.',
  '在脚本程序中修改配置，再回到这里保留到草稿。保存账号后配置才会生效。':
    'Edit the configuration in the script program, then keep it in the draft. Save the account to apply it.'
});

const props = defineProps<{ store: AutomationStore; bindingId: string; bindingRevision: number; scriptRevision: number;
  extensionId: string | undefined; visible: boolean;
  draftBinding?: { userId: string; scriptId: string; scriptRevision: number; candidateId?: string } }>();
const emit = defineEmits<{ close: []; changed: []; draftCandidate: [id: string] }>();
const base = '/control/v1/configuration/editor/';
type Session = ReturnType<typeof readSession>;
type Option = { id: string; title: string };
type Pending = { action: 'open' | 'save' | 'cancel' | 'resume'; id: string; candidate?: string };
const managed = useSurfaceAvailability('automation.binding.editor', () => props.extensionId);
const scope = configurationDrafts(props.store, props.bindingId).child('binding-editor');
const state = scope.retain('session', () => {
  const value = reactive({
    session: null as Session | null, pending: null as Pending | null, options: [] as Option[],
    candidate: '', issue: '', busy: false, canResume: false
  });
  scope.guardCancel(() => !value.session && !value.pending && !value.busy);
  return value;
});
const awaitingResult = computed(() => !!state.pending && !state.busy);
const recovering = computed(() => ['configuration_original_lost', 'configuration_recovery_pending'].includes(state.issue));
const feedback = computed(() => awaitingResult.value ? t('操作结果待确认；请查询状态，勿重复提交。') :
  state.issue && state.issue !== 'configuration_editor_confirm_replacement' ? message(state.issue) : '');

function readSession(value: unknown) {
  const row = objectValue(value);
  return { id: stringValue(row.sessionId), revision: integerValue(row.revision), phase: stringValue(row.phase).toLowerCase() };
}

function readOptions(value: unknown) {
  return arrayValue(value, item => {
    const row = objectValue(item);
    return { id: stringValue(row.id), title: stringValue(row.title) };
  });
}

function message(code: string): string {
  const messages: Record<string, string> = {
    configuration_editor_confirm_replacement: '将绑定更换为以下配置。请确认保存。',
    configuration_editor_multiple_profiles: '只能保留一个配置。请移除多余配置后再保存。',
    configuration_editor_run_identity_ambiguous: '此配置的启动名称与安装目录中的另一套配置重复。请更改名称后再保存。',
    configuration_editor_profile_required: '请保留一个配置后再保存。',
    configuration_editor_existing_profile_required: '请先在脚本程序中创建一套配置，再重新打开编辑。',
    configuration_editor_program_required: '请检查脚本目录与上游程序是否完整。',
    configuration_editor_local_required: '请在 Host 本机打开脚本程序。',
    configuration_original_lost: '脚本实例配置丢失。请在脚本程序中重新创建配置，或导入可用的备份。',
    configuration_recovery_pending: '此脚本实例的配置还在恢复。请在设置中的诊断查看原因。'
  };
  return t(messages[code] || code);
}

function open(candidate?: string): void {
  if (!props.store.can('configuration.native.open')) {
    state.issue = 'configuration_editor_local_required';
    return;
  }
  void send({ action: 'open', id: createIdentity(), candidate });
}

async function send(operation: Pending): Promise<void> {
  if (state.busy) return;
  state.busy = true; state.pending = operation; state.issue = '';
  const session = state.session;
  try {
    if (operation.action === 'open') {
      const response = await props.store.client.write(base + 'open-binding', {
        sessionId: operation.id, bindingId: props.bindingId, expectedBindingRevision: props.bindingRevision,
        expectedScriptRevision: props.scriptRevision, candidateId: operation.candidate,
        draft: props.draftBinding ? { userId: props.draftBinding.userId, scriptId: props.draftBinding.scriptId } : undefined,
        draftCandidateId: props.draftBinding?.candidateId
      }, objectValue, operation.id);
      state.session = response.session == null ? null : readSession(response.session);
      state.canResume = response.canResume === true;
      state.options = readOptions(response.options);
      state.issue = response.issue == null ? '' : stringValue(response.issue);
      state.candidate = '';
    } else if (session) {
      const endpoint = base + session.id + '/';
      const revision = { expectedSessionRevision: session.revision };
      if (operation.action === 'save') {
        const response = await props.store.client.write(endpoint + 'save-binding', {
          ...revision, operationId: operation.id, candidateId: operation.candidate
        }, objectValue, operation.id);
        state.session = readSession(response.session);
        if (typeof response.draftCandidate === 'string') emit('draftCandidate', response.draftCandidate);
        state.canResume = response.canResume === true;
        if (response.inspection != null) {
          const inspection = objectValue(response.inspection);
          state.issue = stringValue(inspection.issue);
          state.options = readOptions(inspection.options);
        }
      } else if (operation.action === 'resume') {
        state.session = await props.store.client.write(endpoint + 'resume-binding',
          { ...revision, nextSessionId: operation.id }, readSession, operation.id);
        state.canResume = false; state.options = [];
      } else {
        state.session = await props.store.client.write(endpoint + 'cancel', {
          ...revision, format: 'nexus.control.native-editor-finish', version: 1, operationId: operation.id
        }, readSession, operation.id);
      }
    }
    state.pending = null;
    if (state.session && ['saved', 'cancelled'].includes(state.session.phase)) {
      const saved = state.session.phase === 'saved';
      state.session = null; state.canResume = false; state.options = [];
      if (saved) {
        if (!props.draftBinding) configurationDrafts(props.store, props.bindingId).changed();
        emit('changed');
      }
      emit('close');
    }
  } catch (error) {
    if (!(error instanceof ControlFailure && error.uncertain)) state.pending = null;
    if (operation.action === 'save' && error instanceof ControlFailure && error.code === 'configuration_draft_missing') {
      state.session = null; state.canResume = false; state.options = [];
    }
    state.issue = error instanceof Error ? error.message : '操作失败，请重试。';
  } finally { state.busy = false; }
}

function finish(action: Pending['action'], candidate?: string): void {
  void send({ action, id: createIdentity(), candidate });
}

watch(() => [props.visible, managed.value] as const, ([visible]) => {
  if (visible && !managed.value && !state.busy && !state.pending && !state.session) open();
}, { immediate: true });
</script>

<template>
  <nexus-modal :open.prop="visible" :locked.prop="state.busy" :title="t('编辑配置')" :size="managed ? 'wide' : 'normal'"
    @nexus-close="emit('close')">
    <ExtensionOutlet v-if="managed" target="automation.binding.editor" :store="store" :visible="visible"
      :entity-id="bindingId" :extension-ids="extensionId ? [extensionId] : []" :draft-binding="draftBinding"
      @draft-candidate="emit('draftCandidate', $event)" />
    <template v-else>
      <nexus-spinner v-if="state.busy && !state.session" :label="t('正在打开脚本程序…')" />
      <p v-if="visible && recovering" role="alert">{{ feedback }}</p>
      <nexus-toast v-else-if="visible && feedback" :tone="awaitingResult ? 'warning' : 'danger'" :message.prop="feedback" />
      <fieldset v-if="!state.session && state.options.length && !state.pending" data-surface="raised">
        <legend>{{ t('请选择要绑定的配置。') }}</legend>
        <div v-for="option in state.options" :key="option.id">
          <label><input v-model="state.candidate" type="radio" :name="'configuration-' + bindingId" :value="option.id" />
            {{ option.title }}</label>
        </div>
      </fieldset>
      <template v-if="state.session">
        <p v-if="state.issue === 'configuration_editor_confirm_replacement'">{{ message(state.issue) }}</p>
        <p v-else-if="state.canResume">{{ t('点击「继续编辑」重新打开脚本程序，或取消本次修改。') }}</p>
        <p v-else>{{ t(draftBinding ? '在脚本程序中修改配置，再回到这里保留到草稿。保存账号后配置才会生效。' :
          '主程序已启动。请在脚本程序中完成修改，回到这里保存为快照。取消将恢复原配置。') }}</p>
        <ul v-if="state.options.length"><li v-for="option in state.options" :key="option.id">{{ option.title }}</li></ul>
      </template>
    </template>
      <button slot="footer" type="button" @click="emit('close')">{{ t('关闭') }}</button>
      <button v-if="state.pending && awaitingResult" slot="footer" type="button" @click="send(state.pending)">{{ t('查询操作状态并刷新') }}</button>
      <template v-else-if="state.session">
        <button slot="footer" type="button" :disabled="state.busy" @click="finish('cancel')">{{ t('取消') }}</button>
        <button v-if="state.canResume" slot="footer" type="button" :disabled="state.busy" @click="finish('resume')">{{ t('继续编辑') }}</button>
        <button v-if="state.issue === 'configuration_editor_confirm_replacement'" slot="footer" type="button" class="primary" :disabled="state.busy"
          @click="finish('save', state.options[0]?.id)">{{ t(draftBinding ? '保留到草稿' : '保存并绑定此配置') }}</button>
        <button v-else-if="!state.canResume" slot="footer" type="button" class="primary" :disabled="state.busy"
          @click="finish('save')">{{ t(draftBinding ? '保留到草稿' : '保存') }}</button>
      </template>
      <button v-else-if="state.options.length" slot="footer" type="button" class="primary" :disabled="state.busy || !state.candidate"
        @click="open(state.candidate)">{{ t('打开编辑器') }}</button>
      <button v-else-if="state.issue && !managed" slot="footer" type="button" :disabled="state.busy" @click="open()">{{ t('重试') }}</button>
  </nexus-modal>
</template>
