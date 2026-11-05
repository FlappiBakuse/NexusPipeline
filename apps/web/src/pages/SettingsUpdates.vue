<script setup lang="ts">
import { onBeforeUnmount, ref, watch } from 'vue';
import type { AppStore } from '../app/store';
import { arrayValue, objectValue, stringValue } from '../control/client';
import { t } from '../app/locale';

const props = defineProps<{ store: AppStore; visible: boolean; channel: 'stable' | 'prerelease' }>();
const busy = ref(false);
const message = ref('尚未检查更新。');
const error = ref('');
let owner: AbortController | undefined;
watch(
  () => [props.visible, props.channel] as const,
  ([visible]) => {
    owner?.abort();
    if (visible) void read();
  },
  { immediate: true }
);
onBeforeUnmount(() => owner?.abort());
function show(value: unknown): void {
  if (value === null) {
    message.value = '尚未检查更新。';
    return;
  }
  const feed = objectValue(value);
  const releases = arrayValue(feed.releases, (item) => stringValue(objectValue(item).hostVersion));
  message.value = releases.length ? t('最新版本：') + releases[0] : '当前没有可用更新。';
}
async function read(): Promise<void> {
  const current = new AbortController();
  owner = current;
  try {
    const value = await props.store.client.read(
      '/control/v1/maintenance/feed/' + props.channel,
      (value) => value,
      current.signal
    );
    current.signal.throwIfAborted();
    show(value);
  } catch (reason) {
    if (!current.signal.aborted) {
      props.store.report(reason);
      error.value = '更新状态读取失败。';
    }
  }
}
async function check(): Promise<void> {
  if (busy.value || !props.store.can('maintenance.write')) return;
  const channel = props.channel;
  busy.value = true;
  error.value = '';
  try {
    const value = await props.store.client.write(
      '/control/v1/maintenance/feed/' + channel + '/refresh',
      null,
      (value) => value
    );
    if (props.visible && props.channel === channel) show(value);
  } catch (reason) {
    if (props.visible && props.channel === channel) {
      props.store.report(reason);
      error.value = '更新检查失败，请重试。';
    }
  } finally {
    busy.value = false;
  }
}
</script>

<template>
  <div class="update-status">
    <dl>
      <div>
        <dt>{{ t('当前版本') }}</dt>
        <dd>v{{ store.state.capabilities?.hostVersion }}</dd>
      </div>
      <div>
        <dt>{{ t('更新渠道') }}</dt>
        <dd>{{ t(channel === 'stable' ? '稳定版 (Stable)' : '预发布 (Pre-release)') }}</dd>
      </div>
    </dl>
    <p role="status">{{ t(message) }}</p>
    <p v-if="error" role="alert">{{ t(error) }}</p>
    <div class="actions">
      <button :disabled="busy || !store.can('maintenance.write')" @click="check">
        {{ t(busy ? '检查中…' : '检查更新') }}
      </button>
    </div>
  </div>
</template>

<style scoped>
.update-status {
  margin-top: -20px;
}
dl,
dd,
p {
  margin: 0;
}
dl > div {
  display: flex;
  gap: 12px;
  min-height: 34px;
  align-items: center;
  border-bottom: 1px solid var(--border);
}
dt {
  width: 94px;
  color: var(--muted);
}
p {
  margin-top: 12px;
  color: var(--muted);
  font-size: 12px;
}
[role='alert'] {
  color: var(--nx-color-danger);
}
.actions {
  display: flex;
  justify-content: flex-end;
  margin-top: 36px;
}
</style>
