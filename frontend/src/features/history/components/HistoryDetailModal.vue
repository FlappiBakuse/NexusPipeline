<script setup lang="ts">
import { nextTick, onBeforeUnmount, ref, watch } from "vue";
import { api, apiBlob, isAbortError } from "../../../platform/api";
import { t } from "../../../platform/i18n";
import { renderPluginSlot } from "@bridge/index";
import { disposePluginSlot } from "@bridge/index";
import NxpBadge from "../../../ui/primitives/NxpBadge.vue";
import NxpButton from "../../../ui/primitives/NxpButton.vue";
import NxpEmptyState from "../../../ui/primitives/NxpEmptyState.vue";
import NxpIcon from "../../../ui/primitives/NxpIcon.vue";
import NxpModal from "../../../ui/primitives/NxpModal.vue";
import NxpScrollArea from "../../../ui/primitives/NxpScrollArea.vue";
import { badgeTone, formatDateTime, statusLabel, statusTone } from "../utils/historyFormat";
import type { HistoryAttempt, HistoryDetailPayload, HistoryLog, HistoryRecord, HistoryScreenshot } from "../utils/historyTypes";

/** 运行详情弹窗：承担详情加载、尝试日志/截图、object URL 生命周期与插件 slot 清理。 */

const props = defineProps<{ record: HistoryRecord | null; fallbackUser?: string }>();
const emit = defineEmits<{ close: [] }>();

const detailData = ref<HistoryDetailPayload | null>(null);
const detailLoading = ref(false);
const detailError = ref("");
const detailSlot = ref<HTMLElement | null>(null);
const imageUrls = ref<Record<string, string>>({});
const lightbox = ref<{ url: string; alt: string; caption: string } | null>(null);
let detailRequestId = 0;

function detailImageKey(attempt: HistoryAttempt, screenshot: HistoryScreenshot, index: number) {
  return `${props.record?.id || "history"}:${attempt.number}:${screenshot.id || index}`;
}
function historyImagePath(record: HistoryRecord, attempt: HistoryAttempt, screenshot: HistoryScreenshot) {
  return screenshot.imageUrl || `/api/history/image?id=${encodeURIComponent(record.id || "")}&attempt=${encodeURIComponent(attempt.number)}&screenshot=${encodeURIComponent(screenshot.id || "")}`;
}
async function loadHistoryImage(key: string, path: string) {
  if (imageUrls.value[key]) return imageUrls.value[key];
  try {
    const blob = await apiBlob(path);
    if (!blob.type.startsWith("image/")) throw new Error(t("history.screenshot.invalid_format"));
    const url = URL.createObjectURL(blob);
    imageUrls.value[key] = url;
    return url;
  } catch (reason) {
    if (!isAbortError(reason)) return "";
    return "";
  }
}
async function hydrateDetailImages() {
  const record = detailData.value?.record;
  if (!record) return;
  const attempts = record.attemptDetails || [];
  const logs = detailData.value?.attemptLogs || [];
  for (const attempt of attempts) {
    const log = logs.find((item) => item.number === attempt.number);
    for (const [index, screenshot] of (attempt.screenshots || log?.screenshots || []).entries()) {
      void loadHistoryImage(detailImageKey(attempt, screenshot, index), historyImagePath(record, attempt, screenshot));
    }
  }
}
async function openImage(attempt: HistoryAttempt, screenshot: HistoryScreenshot, index: number) {
  const record = detailData.value?.record;
  if (!record) return;
  const key = detailImageKey(attempt, screenshot, index);
  const url = await loadHistoryImage(key, historyImagePath(record, attempt, screenshot));
  if (!url) return;
  lightbox.value = {
    url,
    alt: t("history.screenshot.item", { attempt: attempt.number, index: index + 1 }),
    caption: [
      screenshot.width && screenshot.height ? `${screenshot.width}×${screenshot.height}` : "",
      screenshot.trigger || "",
    ].filter(Boolean).join(" · "),
  };
}
async function loadFullLog(attemptNumber: number) {
  const record = detailData.value?.record;
  if (!record?.id) return;
  try {
    const data = (await api(
      "GET",
      `/api/history/detail?id=${encodeURIComponent(record.id)}&full=true&attempt=${encodeURIComponent(attemptNumber)}`,
    )) as HistoryDetailPayload;
    const full = data.attemptLogs?.find((item) => item.number === attemptNumber);
    if (!full) return;
    const current = detailData.value?.attemptLogs || [];
    detailData.value = { ...detailData.value, attemptLogs: current.map((item) => (item.number === attemptNumber ? { ...item, ...full } : item)) };
  } catch (reason) {
    if (!isAbortError(reason)) detailError.value = reason instanceof Error ? reason.message : String(reason);
  }
}
function attemptLog(attemptNumber: number): HistoryLog | undefined {
  return detailData.value?.attemptLogs?.find((item) => item.number === attemptNumber);
}
function attemptScreenshots(attempt: HistoryAttempt) {
  return attempt.screenshots?.length ? attempt.screenshots : attemptLog(attempt.number)?.screenshots || [];
}
function attemptLogText(attemptNumber: number) {
  const log = attemptLog(attemptNumber);
  return log?.logText || log?.logTail || t("history.no_script_log");
}
function attemptLogIsTail(attemptNumber: number) {
  const log = attemptLog(attemptNumber);
  return Boolean(log && log.logText == null && Number(log.logTotalLines || 0) > 200);
}

function revokeImages() {
  for (const url of Object.values(imageUrls.value)) URL.revokeObjectURL(url);
  imageUrls.value = {};
}

async function load() {
  const record = props.record;
  if (!record) return;
  const id = ++detailRequestId;
  detailData.value = null;
  detailError.value = "";
  detailLoading.value = true;
  try {
    const data = (await api("GET", `/api/history/detail?id=${encodeURIComponent(record.id || "")}`)) as HistoryDetailPayload;
    if (id !== detailRequestId) return;
    detailData.value = data?.record ? data : { record, attemptLogs: [] };
    await nextTick();
    if (detailSlot.value) {
      await renderPluginSlot(detailSlot.value, "history.detail.sections", { mode: "detail", primaryId: detailData.value.record?.id || "" });
    }
    await hydrateDetailImages();
  } catch (reason) {
    if (id !== detailRequestId || isAbortError(reason)) return;
    detailError.value = reason instanceof Error ? reason.message : String(reason);
  } finally {
    if (id === detailRequestId) detailLoading.value = false;
  }
}

function close() {
  detailRequestId += 1;
  if (detailSlot.value) void disposePluginSlot(detailSlot.value);
  revokeImages();
  lightbox.value = null;
  detailData.value = null;
  detailError.value = "";
  emit("close");
}

watch(
  () => props.record,
  (record) => {
    revokeImages();
    if (record) void load();
  },
  { immediate: true },
);

onBeforeUnmount(() => {
  detailRequestId += 1;
  if (detailSlot.value) void disposePluginSlot(detailSlot.value);
  revokeImages();
});
</script>

<template>
  <NxpModal
    :open="Boolean(record)"
    :title="record ? `${record.scriptName || t('history.run_records')} ${t('history.run_details')}` : t('history.run_details')"
    size="wide"
    panel-class="secondary-surface"
    body-class="history-detail-body"
    @close="close"
  >
    <template v-if="record">
      <NxpEmptyState v-if="detailLoading" :title="t('common.loading')" />
      <NxpEmptyState v-else-if="detailError" :title="t('history.run_details')" :description="detailError" tone="danger" />
      <template v-else-if="detailData && detailData.record">
        <div class="history-detail-meta" data-testid="history-detail-meta">
          <div class="history-detail-meta-item"><span class="k">{{ t("history.result") }}</span><NxpBadge :tone="statusTone(detailData.record.status)">{{ statusLabel(detailData.record.status) }}</NxpBadge></div>
          <div class="history-detail-meta-item"><span class="k">{{ t("history.run_mode") }}</span><span>{{ detailData.record.mode === "auto" ? t("common.automatic_run") : t("history.run_manually") }}</span></div>
          <div class="history-detail-meta-item"><span class="k">{{ t("history.run_users") }}</span><span>{{ detailData.record.userName || props.fallbackUser }}</span></div>
          <div class="history-detail-meta-item"><span class="k">{{ t("history.attempts") }}</span><span>{{ detailData.record.attemptDetails?.length || detailData.record.attempts || 0 }} / {{ detailData.record.maxAttempts || "-" }}</span></div>
          <div class="history-detail-meta-item"><span class="k">{{ t("history.start_time") }}</span><span>{{ formatDateTime(detailData.record.startTime) }}</span></div>
          <div class="history-detail-meta-item"><span class="k">{{ t("history.end_time") }}</span><span>{{ formatDateTime(detailData.record.endTime) }}</span></div>
          <div class="history-detail-meta-item history-detail-meta-wide"><span class="k">{{ t("history.result_description") }}</span><span>{{ detailData.record.resultDetail || "-" }}</span></div>
        </div>
        <section v-if="detailData.record.pluginHistory?.length" class="plugin-history-section">
          <div class="section-heading"><h3>{{ t("history.detail.plugin_info") }}</h3><span class="muted">{{ t("history.screenshot.snapshot_saved") }}</span></div>
          <section v-for="item in detailData.record.pluginHistory" :key="item.id || item.pluginName || item.title" class="subsection plugin-history-detail">
            <div class="section-heading"><h3>{{ item.title || item.id || t("history.plugin_information") }}</h3><span class="muted">{{ item.pluginDisplayName || item.pluginName || "" }}</span></div>
            <div v-if="item.badges?.length" class="plugin-contribution-badge"><NxpBadge v-for="badge in item.badges" :key="badge.label" :tone="badgeTone(badge.tone)" :title="badge.title">{{ badge.label }}</NxpBadge></div>
            <div v-if="item.fields?.length" class="detail"><div v-for="field in item.fields" :key="field.label" class="kv"><span class="k">{{ field.label || "" }}</span><span>{{ field.value || "" }}</span></div></div>
          </section>
        </section>
        <div ref="detailSlot" class="plugin-slot history-detail-plugin-slot" data-plugin-slot="history.detail.sections" data-plugin-anchor="history.detail.sections" data-plugin-mode="detail" :data-plugin-primary-id="detailData.record.id" hidden></div>
        <div class="history-attempt-list">
          <section v-for="attempt in detailData.record.attemptDetails || []" :key="attempt.number" class="subsection history-attempt-detail">
            <div class="section-heading"><h3>{{ t("common.run.attempt", { attempt: attempt.number }) }}</h3><NxpBadge :tone="statusTone(attempt.status)">{{ statusLabel(attempt.status) }}</NxpBadge></div>
            <div class="history-attempt-meta"><div><span class="k">{{ t("common.time") }}</span><span>{{ formatDateTime(attempt.startTime) }} - {{ formatDateTime(attempt.endTime) }}</span></div><div><span class="k">{{ t("common.reason") }}</span><span>{{ attempt.reason || "-" }}</span></div></div>
            <div v-if="attemptLog(attempt.number)" class="history-log" data-history-log>
              <div class="qk-row">{{ attemptLogIsTail(attempt.number) ? t("history.log.lines_summary.tail", { label: t("history.log.attempt", { attempt: attempt.number }), count: attemptLog(attempt.number)?.logTotalLines || 0, lines: t("history.lines") }) : t("history.log.lines_summary", { label: t("history.log.attempt", { attempt: attempt.number }), count: attemptLog(attempt.number)?.logTotalLines || 0, lines: t("history.lines") }) }}</div>
              <div v-if="attemptLogIsTail(attempt.number)" class="history-log-actions"><span class="muted">{{ t("history.log.tail_only") }}</span><NxpButton class="ghost sm" type="button" @click.stop="loadFullLog(attempt.number)">{{ t("history.view_full_log") }}</NxpButton></div>
              <NxpScrollArea class="history-log-scroll" direction="both" :aria-label="t('history.log.attempt', { attempt: attempt.number })"><pre class="logbox" data-history-log-body>{{ attemptLogText(attempt.number) }}</pre></NxpScrollArea>
            </div>
            <div v-if="attemptScreenshots(attempt).length" class="history-attempt-screenshots" data-testid="history-attempt-screenshots">
              <div class="qk-row">{{ t("history.screenshots.summary", { count: attemptScreenshots(attempt).length }) }}</div>
              <NxpScrollArea class="history-screenshot-strip" direction="horizontal" role="list" :aria-label="t('history.screenshot.attempt_summary', { attempt: attempt.number })">
                <button v-for="(screenshot, index) in attemptScreenshots(attempt)" :key="screenshot.id || index" class="history-screenshot-thumb" type="button" :aria-label="t('history.screenshot.item', { attempt: attempt.number, index: index + 1 })" @click.stop="openImage(attempt, screenshot, index)">
                  <img :src="imageUrls[detailImageKey(attempt, screenshot, index)] || undefined" :alt="t('history.screenshot.item', { attempt: attempt.number, index: index + 1 })" loading="lazy">
                  <span class="history-screenshot-index">{{ index + 1 }}</span>
                </button>
              </NxpScrollArea>
            </div>
          </section>
          <NxpEmptyState v-if="!(detailData.record.attemptDetails || []).length" :title="t('history.attempts')" :description="t('history.no_script_log')" />
        </div>
      </template>
    </template>
    <template #footer>
      <NxpButton class="ghost" type="button" @click.stop="close">{{ t("common.close") }}</NxpButton>
    </template>
  </NxpModal>
  <Teleport to="body">
    <div v-if="lightbox" class="history-image-lightbox" role="dialog" aria-modal="true" :aria-label="t('history.view_run_screenshot')" @click.self="lightbox = null">
      <div class="history-image-lightbox-backdrop" @click="lightbox = null"></div>
      <figure class="history-image-lightbox-content"><img :src="lightbox.url" :alt="lightbox.alt"><figcaption>{{ lightbox.caption }}</figcaption></figure>
      <button class="icon-button history-image-lightbox-close" type="button" :aria-label="t('history.screenshot.close')" @click="lightbox = null"><NxpIcon name="close" /></button>
    </div>
  </Teleport>
</template>
