<script setup lang="ts">
import { computed, onMounted, ref } from "vue";
import { api, isAbortError } from "../../../platform/api";
import { t } from "../../../platform/i18n";
import { toast } from "../../../platform/toast";
import NxpBadge from "../../../ui/primitives/NxpBadge.vue";
import type { DiagnosticsData } from "../utils/settingsTypes";

/** 系统诊断卡片：独立承担诊断结果加载、导出与结果投影。 */

const diagnostics = ref<DiagnosticsData | null>(null);
const loading = ref(false);

const checks = computed(() => (Array.isArray(diagnostics.value?.checks) ? diagnostics.value!.checks! : []));
const attentionCount = computed(() => checks.value.filter((item) => ["warn", "fail"].includes(item.status || "")).length);

function statusTone(value?: string): "ok" | "warn" | "bad" | "muted" {
  return value === "pass" ? "ok" : value === "fail" ? "bad" : value === "warn" ? "warn" : "muted";
}
function statusLabel(value?: string) {
  return t(
    `diagnostics.status.${value || "unknown"}`,
    {},
    { pass: "Normal", warn: "Attention", fail: "Failed", skipped: "Skipped" }[value || ""] ||
      value ||
      "Unknown",
  );
}
function checkLabel(id?: string) {
  const key = String(id || "").replaceAll(".", "_").replaceAll("-", "_");
  const fallback = String(id || "")
    .split(".")
    .map((part) => part.replaceAll("-", " "))
    .join(" · ");
  return t(`diagnostics.check.${key}`, {}, fallback || t("diagnostics.detail", {}, "Diagnostic check"));
}
function categoryLabel(category?: string) {
  const key = String(category || "").trim();
  return key ? t(`diagnostics.category.${key}`, {}, key) : "";
}
function diagnosticText(code?: string, args?: Record<string, unknown>, fallback = "") {
  if (!code) return fallback;
  const value = t(code, args || {}, "");
  if (value !== code) return value;
  if (code.includes(".summary.")) return fallback || t("diagnostics.summary", {}, "Summary");
  if (code.endsWith(".detail")) return t("diagnostics.detail", {}, fallback);
  if (code.endsWith(".remediation")) return t("diagnostics.remediation", {}, fallback);
  return fallback;
}

async function loadDiagnostics() {
  loading.value = true;
  try {
    diagnostics.value = (await api("GET", "/api/diagnostics")) as DiagnosticsData;
  } catch (reason) {
    if (!isAbortError(reason)) {
      diagnostics.value = { error: reason instanceof Error ? reason.message : String(reason) };
    }
  } finally {
    loading.value = false;
  }
}
async function exportDiagnostics() {
  try {
    const result = (await api("POST", "/api/diagnostics/export")) as { path?: string } | null;
    toast(t("settings.diagnostics.exported", { path: result?.path || t("settings.generated") }));
  } catch (reason) {
    if (!isAbortError(reason)) toast(reason instanceof Error ? reason.message : String(reason), "error");
  }
}

onMounted(() => {
  void loadDiagnostics();
});

defineExpose({ reload: loadDiagnostics });
</script>

<template>
  <div class="diagnostics-section">
    <div class="row-actions">
      <button class="ghost" type="button" data-testid="load-diagnostics" :disabled="loading" @click="loadDiagnostics">
        {{ t("settings.refresh_diagnostics") }}</button
      ><button class="ghost" type="button" data-testid="export-diagnostics" @click="exportDiagnostics">
        {{ t("settings.diagnostics.export_help") }}
      </button>
    </div>
    <div id="diagnostics-status" class="diagnostics-status" data-testid="diagnostics-status" aria-live="polite">
      <p v-if="loading" class="muted">{{ t("settings.loading_diagnostics") }}</p>
      <p v-else-if="diagnostics?.error" class="callout callout-warning">{{ diagnostics.error }}</p>
      <template v-else-if="diagnostics"
        ><div class="diagnostics-overview">
          <div class="diagnostics-overview-status">
            <span class="diagnostics-overview-label">{{ t("diagnostics.overall", {}, "Overall status") }}</span
            ><NxpBadge :tone="statusTone(diagnostics.overallStatus || 'warn')">{{ statusLabel(diagnostics.overallStatus || "warn") }}</NxpBadge
            ><span class="muted">v{{ diagnostics.hostVersion || "" }}</span>
          </div>
          <span class="muted diagnostics-overview-meta">{{
            attentionCount > 0
              ? t("settings.diagnostics.overview_attention", { total: checks.length, count: attentionCount })
              : t("settings.diagnostics.overview_clear", { total: checks.length, count: 0 })
          }}</span>
        </div>
        <div class="diagnostics-table" role="table" :aria-label="t('settings.diagnostics.system_checks')">
          <div class="diagnostics-table-header" role="row">
            <span role="columnheader">{{ t("common.check") }}</span
            ><span role="columnheader">{{ t("common.status") }}</span
            ><span role="columnheader">{{ t("settings.diagnostics") }}</span>
          </div>
          <div
            v-for="check in checks"
            :key="check.id"
            class="diagnostic-row"
            role="row"
            :data-diagnostic-status="check.status || 'unknown'"
          >
            <div class="diagnostic-check-name" role="cell">
              <div class="diagnostic-check-title-line">
                <strong class="diagnostic-check-title">{{ checkLabel(check.id) }}</strong
                ><NxpBadge tone="muted">{{ categoryLabel(check.category) }}</NxpBadge>
              </div>
              <span class="muted diagnostic-check-id mono">{{ check.id || "" }}</span>
            </div>
            <div class="diagnostic-check-status" role="cell">
              <NxpBadge :tone="statusTone(check.status)">{{ statusLabel(check.status) }}</NxpBadge>
            </div>
            <div class="diagnostic-check-info" role="cell">
              <div class="diagnostic-check-summary">
                <span class="diagnostic-info-label">{{ t("diagnostics.summary", {}, "Summary") }}</span
                ><span>{{ diagnosticText(check.summaryCode, check.summaryArgs, statusLabel(check.status)) }}</span>
              </div>
              <div v-if="check.detailCode" class="diagnostic-check-detail">
                <span class="diagnostic-info-label">{{ t("diagnostics.detail", {}, "Details") }}</span
                ><span>{{ diagnosticText(check.detailCode, check.detailArgs) }}</span>
              </div>
              <div v-if="check.remediationCode" class="diagnostic-check-remediation">
                <span class="diagnostic-info-label">{{ t("diagnostics.remediation", {}, "Recommendation") }}</span
                ><span>{{ diagnosticText(check.remediationCode, check.remediationArgs) }}</span>
              </div>
            </div>
          </div>
          <div v-if="!checks.length" class="diagnostics-empty" role="row">
            {{ t("diagnostics.empty", {}, "No diagnostic results") }}
          </div>
        </div></template
      >
    </div>
  </div>
</template>
