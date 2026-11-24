<script setup lang="ts">
import { computed, reactive, watch } from 'vue';
import { arrayValue, ControlFailure, integerValue, objectValue, stringValue, type Decoder } from '../control/client';
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
type Pending = Readonly<{
  action: 'open' | 'save' | 'cancel' | 'resume';
  id: string;
  route: string;
  body: Readonly<Record<string, unknown>>;
  draft: boolean;
}>;
const managed = useSurfaceAvailability('automation.binding.editor', () => props.extensionId);
const configuration = configurationDrafts(props.store, props.bindingId);
const scope = configuration.child('binding-editor');
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
  const id = createIdentity(), draft = props.draftBinding;
  submit('open', id, base + 'open-binding', {
    sessionId: id, bindingId: props.bindingId, expectedBindingRevision: props.bindingRevision,
    expectedScriptRevision: props.scriptRevision, candidateId: candidate,
    draft: draft ? Object.freeze({ userId: draft.userId, scriptId: draft.scriptId }) : undefined,
    draftCandidateId: draft?.candidateId
  });
}

function submit(action: Pending['action'], id: string, route: string, body: Record<string, unknown>): void {
  if (state.pending || state.busy) return;
  void send(Object.freeze({ action, id, route, body: Object.freeze(body), draft: !!props.draftBinding }));
}

async function send(operation: Pending): Promise<void> {
  if (state.busy || state.pending && state.pending !== operation) return;
  state.busy = true; state.pending = operation; state.issue = '';
  const write = <T>(decode: Decoder<T>) =>
    props.store.client.write(operation.route, operation.body, decode, operation.id);
  try {
    if (operation.action === 'open') {
      const result = await write(value => {
        const response = objectValue(value);
        return { session: response.session == null ? null : readSession(response.session), canResume: response.canResume === true,
          options: readOptions(response.options), issue: response.issue == null ? '' : stringValue(response.issue) };
      });
      Object.assign(state, result);
      state.candidate = '';
    } else if (operation.action === 'save') {
      const result = await write(value => {
        const response = objectValue(value);
        const inspection = response.inspection == null ? null : objectValue(response.inspection);
        return { session: readSession(response.session), canResume: response.canResume === true,
          draftCandidate: response.draftCandidate,
          inspection: inspection && { issue: stringValue(inspection.issue), options: readOptions(inspection.options) } };
      });
      state.session = result.session;
      state.canResume = result.canResume;
      if (result.inspection) Object.assign(state, result.inspection);
      if (typeof result.draftCandidate === 'string') emit('draftCandidate', result.draftCandidate);
    } else {
      state.session = await write(readSession);
      if (operation.action === 'resume') { state.canResume = false; state.options = []; }
    }
    state.pending = null;
    if (state.session && ['saved', 'cancelled'].includes(state.session.phase)) {
      const saved = state.session.phase === 'saved';
      state.session = null; state.canResume = false; state.options = [];
      if (saved) {
        if (!operation.draft) configuration.changed();
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

function finish(action: Exclude<Pending['action'], 'open'>, candidate?: string): void {
  const session = state.session;
  if (!session) return;
  const id = createIdentity(), endpoint = base + session.id + '/';
  const revision = { expectedSessionRevision: session.revision };
  if (action === 'save') submit(action, id, endpoint + 'save-binding', { ...revision, operationId: id, candidateId: candidate });
  else if (action === 'resume') submit(action, id, endpoint + 'resume-binding', { ...revision, nextSessionId: id });
  else submit(action, id, endpoint + 'cancel',
    { ...revision, format: 'nexus.control.native-editor-finish', version: 1, operationId: id });
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
