<script setup lang="ts">
import { ref } from 'vue';
import type { AutomationBundle } from '../control/wire.generated';
import { t } from '../app/locale';

type User = AutomationBundle['users'][number];
const props = defineProps<{ user: User; revision: number; catalogRevision: number; disabled: boolean }>();
const emit = defineEmits<{
  change: [user: User, revision: number, catalogRevision: number];
  error: [message: string];
}>();
const picker = ref<HTMLInputElement>();
const busy = ref(false);
async function choose(event: Event): Promise<void> {
  const input = event.target as HTMLInputElement;
  const file = input.files?.[0];
  input.value = '';
  if (!file || busy.value || props.disabled) return;
  const user = { ...props.user },
    revision = props.revision,
    catalogRevision = props.catalogRevision;
  busy.value = true;
  let bitmap: ImageBitmap | undefined;
  try {
    if (
      file.size > 10 * 1024 * 1024 ||
      !['image/png', 'image/jpeg', 'image/webp', 'image/gif'].includes(file.type)
    )
      throw new Error('请选择不超过 10 MiB 的 PNG、JPEG、WebP 或 GIF 图片。');
    bitmap = await createImageBitmap(file);
    const side = Math.min(bitmap.width, bitmap.height);
    const canvas = document.createElement('canvas');
    canvas.width = 96;
    canvas.height = 96;
    const context = canvas.getContext('2d')!;
    context.drawImage(
      bitmap,
      (bitmap.width - side) / 2,
      (bitmap.height - side) / 2,
      side,
      side,
      0,
      0,
      96,
      96
    );
    emit('change', { ...user, avatar: canvas.toDataURL('image/png') }, revision, catalogRevision);
    canvas.width = 0;
    canvas.height = 0;
  } catch (reason) {
    emit('error', reason instanceof Error ? reason.message : '头像图片无法读取。');
  } finally {
    bitmap?.close();
    busy.value = false;
  }
}
</script>

<template>
  <span class="avatar-control">
    <button
      type="button"
      :disabled="disabled || busy"
      :aria-label="t('上传头像') + ' ' + user.name"
      @click="picker?.click()"
    >
      <img v-if="user.avatar" :src="user.avatar" alt="" />
      <span v-else>{{ user.name.slice(0, 1) }}</span>
      <small>+</small>
    </button>
    <input
      ref="picker"
      type="file"
      accept="image/png,image/jpeg,image/webp,image/gif"
      tabindex="-1"
      aria-hidden="true"
      @change="choose"
    />
  </span>
</template>

<style scoped>
.avatar-control {
  display: inline-flex;
  flex: none;
  margin-right: 4px;
}
button {
  display: grid;
  place-items: center;
  position: relative;
  padding: 0;
  width: 36px;
  height: 36px;
  min-height: 36px;
  border: 1px solid var(--border);
  border-radius: 6px;
  background: var(--content-control);
  color: var(--accent);
}
img {
  width: 34px;
  height: 34px;
  object-fit: cover;
  border-radius: 5px;
}
small {
  display: grid;
  place-items: center;
  position: absolute;
  right: -6px;
  bottom: -6px;
  width: 17px;
  height: 17px;
  border: 1px solid var(--border);
  border-radius: 50%;
  background: var(--panel);
}
input {
  display: none;
}
</style>
