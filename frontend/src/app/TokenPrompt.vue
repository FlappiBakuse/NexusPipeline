<script setup lang="ts">
import { computed, nextTick, ref, watch } from "vue";
import NxpModal from "../ui/primitives/NxpModal.vue";
import NxpButton from "../ui/primitives/NxpButton.vue";
import NxpTextInput from "../ui/primitives/NxpTextInput.vue";
import { authPromptText, reloadPage, verifyToken } from "../platform/auth";

const props = defineProps<{ open: boolean }>();
const emit = defineEmits<{ close: [] }>();

const token = ref("");
const error = ref("");
const busy = ref(false);
const input = ref<InstanceType<typeof NxpTextInput> | null>(null);

const title = computed(() => authPromptText.title());
const copy = computed(() => authPromptText.copy());
const placeholder = computed(() => authPromptText.placeholder());

watch(() => props.open, async value => {
  if (!value) return;
  error.value = "";
  await nextTick();
  input.value?.focus();
});

async function submit() {
  if (busy.value) return;
  const value = token.value.trim();
  if (!value) {
    error.value = authPromptText.requiredInput();
    input.value?.focus();
    return;
  }
  busy.value = true;
  try {
    if (await verifyToken(value)) {
      reloadPage();
      return;
    }
    error.value = authPromptText.invalid();
  } finally {
    busy.value = false;
  }
}
</script>

<template>
  <NxpModal :open="props.open" :title="title" :aria-label="title" locked data-testid="token-prompt" @close="emit('close')">
    <form id="token-form" class="token-form" @submit.prevent="submit">
      <p class="modal-copy">{{ copy }}</p>
      <label class="field-label" for="token-input">{{ placeholder }}</label>
      <NxpTextInput
        id="token-input"
        ref="input"
        v-model="token"
        type="password"
        autocomplete="off"
        :placeholder="placeholder"
        :aria-label="placeholder"
        :aria-invalid="error ? 'true' : undefined"
        :aria-describedby="error ? 'token-error' : undefined"
      />
      <p id="token-error" class="req" role="alert" aria-live="polite">{{ error }}</p>
    </form>
    <template #footer>
      <NxpButton tone="primary" :disabled="busy" @click="submit">{{ authPromptText.enter() }}</NxpButton>
    </template>
  </NxpModal>
</template>
