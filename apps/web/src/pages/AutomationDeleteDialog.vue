<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import ModalTransition from '../components/ModalTransition.vue';
import { t } from '../app/locale';
import type { EntityKind } from './automation-editor';

const props = defineProps<{ target: { name: string } | null; kind: EntityKind; visible: boolean }>();
const emit = defineEmits<{ confirm: [name?: string]; cancel: [] }>();
const input = ref('');
watch(() => props.target, () => { input.value = ''; });
const titles = { users: '删除用户', scripts: '删除脚本实例', queues: '删除调度队列' };
const consequences = {
  users: '删除「{name}」会解除其全部脚本绑定，并移除这些绑定的当前配置。',
  scripts: '删除「{name}」会移除相关账号绑定及其当前配置，并将此实例从队列中移除。',
  queues: '删除「{name}」也会移除该队列的定时计划。'
};
const message = computed(() => t(consequences[props.kind]).replace('{name}', () => props.target?.name ?? ''));
const matches = computed(() => !!props.target && input.value === props.target.name);
</script>

<template>
  <ModalTransition>
    <nexus-modal v-if="target && kind === 'users'" :open.prop="visible" :title="t(titles.users)" @nexus-close="emit('cancel')">
      <div class="account-confirmation">
        <p>{{ message }}</p>
        <label>
          {{ t('输入完整用户名以确认删除') }}
          <input v-model="input" type="text" autocomplete="off" autocapitalize="off" spellcheck="false" />
        </label>
      </div>
      <div slot="footer" class="confirmation-actions">
        <button type="button" autofocus @click="emit('cancel')">{{ t('取消') }}</button>
        <button type="button" class="danger" :disabled="!matches"
          @click="matches && emit('confirm', input)">{{ t('确认删除') }}</button>
      </div>
    </nexus-modal>
    <nexus-confirm-dialog v-else-if="target" :open.prop="visible" :title="t(titles[kind])"
      :message="message" tone="danger" @nexus-confirm="emit('confirm')"
      @nexus-cancel="emit('cancel')" @nexus-close="emit('cancel')" />
  </ModalTransition>
</template>

<style scoped>
.account-confirmation, label {
  display: grid;
  gap: 8px;
}
.account-confirmation { gap: 16px; overflow-wrap: anywhere; }
p { margin: 0; }
.confirmation-actions { display: flex; gap: 12px; }
.confirmation-actions > button { min-width: 80px; }
</style>
