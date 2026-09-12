<script setup lang="ts">
import NxpButton from "../primitives/NxpButton.vue";
import NxpModal from "../primitives/NxpModal.vue";

const props = withDefaults(defineProps<{
  open?: boolean;
  title: string;
  message?: string;
  confirmLabel?: string;
  cancelLabel?: string;
  confirmTone?: "primary" | "danger";
  busy?: boolean;
}>(), {
  open: false,
  message: "",
  confirmLabel: "Confirm",
  cancelLabel: "Cancel",
  confirmTone: "primary",
  busy: false,
});

const emit = defineEmits<{
  close: [];
  cancel: [];
  confirm: [];
}>();

function close() {
  if (!props.busy) emit("close");
}

function cancel() {
  if (!props.busy) emit("cancel");
}
</script>

<template>
  <NxpModal
    :open="props.open"
    :title="props.title"
    :locked="props.busy"
    :closeable="!props.busy"
    :aria-label="props.title"
    @close="close"
  >
    <p v-if="props.message" class="nxp-confirm-message">{{ props.message }}</p>
    <slot />
    <template #footer>
      <NxpButton class="ghost" type="button" :disabled="props.busy" @click="cancel">{{ props.cancelLabel }}</NxpButton>
      <NxpButton
        type="button"
        :class="{ primary: props.confirmTone === 'primary', danger: props.confirmTone === 'danger' }"
        :disabled="props.busy"
        @click="emit('confirm')"
      >
        {{ props.confirmLabel }}
      </NxpButton>
    </template>
  </NxpModal>
</template>

<style>
.nxp-confirm-message { margin: 0; line-height: 1.6; white-space: pre-wrap; }
</style>
