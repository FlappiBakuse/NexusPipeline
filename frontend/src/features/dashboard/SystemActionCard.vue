<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from "vue";
import { api, isAbortError } from "../../platform/api";
import { toast } from "../../platform/toast";
import { t } from "../../platform/i18n";

interface SystemAction {
  action?: string;
  deadline?: string;
  queueName?: string;
}

const props = defineProps<{ action: SystemAction }>();
const emit = defineEmits<{ cancelled: [] }>();
const now = ref(Date.now());
const cancelling = ref(false);
let timer: ReturnType<typeof setInterval> | null = null;

const verb = computed(() => t(
  props.action.action === "sleep"
    ? "common.sleep"
    : props.action.action === "reboot"
      ? "common.restart"
      : "common.shut_down",
));

const deadline = computed(() => new Date(props.action.deadline || "").getTime());
const remainingSeconds = computed(() => {
  const target = deadline.value;
  if (!Number.isFinite(target)) return 0;
  return Math.max(0, Math.round((target - now.value) / 1000));
});
const countdownText = computed(() => remainingSeconds.value > 0
  ? t("common.status.until_action", { seconds: remainingSeconds.value, verb: verb.value })
  : t("common.status.executing_soon", { verb: verb.value }));
const statusText = computed(() => t("common.status.queue_complete", {
  queueName: props.action.queueName || "",
  countdown: countdownText.value,
}));

async function cancel() {
  if (cancelling.value) return;
  cancelling.value = true;
  try {
    await api("POST", "/api/system-action/cancel");
    toast(t("common.status.cancelled", { verb: verb.value }));
    emit("cancelled");
  } catch (error) {
    if (!isAbortError(error)) toast(error instanceof Error ? error.message : String(error), "error");
  } finally {
    cancelling.value = false;
  }
}

onMounted(() => {
  timer = setInterval(() => { now.value = Date.now(); }, 1000);
});

onBeforeUnmount(() => {
  if (timer) clearInterval(timer);
});
</script>

<template>
  <section class="card section-surface system-action-card" role="status" aria-live="polite" data-testid="system-action-card" :data-action-verb="verb">
    <div class="section-heading">
      <h3>{{ t("common.completion_action_countdown") }}</h3>
      <span class="muted">{{ t("common.status.queue_action_pending") }}</span>
    </div>
    <p class="countdown-text"><span data-testid="system-action-countdown" :data-deadline="action.deadline || ''" :data-queue-name="action.queueName || ''">{{ statusText }}</span></p>
    <div class="qk-row"><button class="danger" type="button" :disabled="cancelling" @click="cancel">{{ t("common.action.cancel_verb", { verb }) }}</button></div>
  </section>
</template>
