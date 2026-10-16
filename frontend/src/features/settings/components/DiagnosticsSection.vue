<script setup lang="ts">
import { computed, onMounted, ref } from "vue";
import { api, isAbortError } from "../../../platform/api";
import { t } from "../../../platform/i18n";
import { toast } from "../../../platform/toast";
import NxpBadge from "../../../ui/primitives/NxpBadge.vue";
import NxpLoadingState from "../../../ui/composites/NxpLoadingState.vue";
import NxpEmptyState from "../../../ui/primitives/NxpEmptyState.vue";
import NxpButton from "../../../ui/primitives/NxpButton.vue";
import type { DiagnosticCheck, DiagnosticsData } from "../utils/settingsTypes";

/** 系统诊断卡片：独立承担诊断结果加载、导出与结果投影。 */

const diagnostics = ref<DiagnosticsData | null>(null);
const loading = ref(false);

const checks = computed(() => (Array.isArray(diagnostics.value?.checks) ? diagnostics.value!.checks! : []));
const attentionCount = computed(() => checks.value.filter((item) => ["warn", "fail"].includes(item.status || "")).length);
const groupedChecks = computed(() => {
  const groups = new Map<string, { key: string; label: string; checks: DiagnosticCheck[] }>();
  checks.value.forEach((check) => {
    const rawCategory = String(check.category || "").trim();
    const key = rawCategory || "uncategorized";
    let group = groups.get(key);
    if (!group) {
      group = { key, label: categoryLabel(rawCategory), checks: [] };
      groups.set(key, group);
    }
    group.checks.push(check);
  });
  return [...groups.values()];
});

function statusTone(value?: string): "ok" | "warn" | "bad" | "muted" {
  return value === "pass" ? "ok" : value === "fail" ? "bad" : value === "warn" ? "warn" : "muted";
}
function statusLabel(value?: string) {
  return t(
    `diagnostics.status.${value || "unknown"}`,
    {},
    { pass: "Normal", warn: "Attention", fail: "Failed", skipped: "Skipped" }[value || ""] ||
      (value && value !== "unknown" ? value : t("common.unknown")),
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
  return key ? t(`diagnostics.category.${key}`, {}, key) : t("diagnostics.category.uncategorized", {}, "Other");
}
function requiresDetail(check: DiagnosticCheck) {
  return ["warn", "fail"].includes(check.status || "");
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

function groupStatus(items: DiagnosticCheck[]) {
  if (items.some(check => check.status === "fail")) return "fail";
  if (items.some(check => check.status === "warn")) return "warn";
  if (items.every(check => check.status === "pass")) return "pass";
  return items.every(check => ["pass", "skipped"].includes(check.status || "")) ? "skipped" : "unknown";
}
function checkSummary(check: DiagnosticCheck) {
  return diagnosticText(check.summaryCode, check.summaryArgs, statusLabel(check.status));
}
function checkDetail(check: DiagnosticCheck) {
  const detail = requiresDetail(check) ? diagnosticText(check.detailCode, check.detailArgs) : "";
  return detail === checkSummary(check) ? "" : detail;
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
    <div id="diagnostics-status" class="assessment-layout" data-testid="diagnostics-status" aria-live="polite" :aria-busy="loading">
      <NxpLoadingState v-if="loading" :title="t('settings.loading_diagnostics')" />
      <NxpEmptyState v-else-if="diagnostics?.error" :title="diagnostics.error" tone="danger" />
      <template v-else-if="diagnostics">
        <header class="assessment-overview">
          <div class="assessment-overview-title">
            <div class="diagnostics-overview-copy"><strong>{{ t('diagnostics.overall') }}</strong><p>{{ attentionCount > 0 ? t('settings.diagnostics.overview_attention', { total: checks.length, count: attentionCount }) : t('settings.diagnostics.overview_clear', { total: checks.length, count: 0 }) }}</p></div>
            <NxpBadge :tone="statusTone(diagnostics.overallStatus)">{{ statusLabel(diagnostics.overallStatus) }}</NxpBadge>
          </div>
        </header>
        <section v-for="(group, groupIndex) in groupedChecks" :key="group.key" class="assessment-group" :aria-label="group.label" :data-diagnostic-category="group.key">
          <header class="assessment-group-heading"><span class="assessment-index" aria-hidden="true">{{ groupIndex + 1 }}</span><h4>{{ group.label }}</h4><NxpBadge :tone="statusTone(groupStatus(group.checks))">{{ statusLabel(groupStatus(group.checks)) }}</NxpBadge></header>
          <div role="table" :aria-label="group.label">
            <div class="assessment-row assessment-table-header" role="row"><span role="columnheader">{{ t('common.check') }}</span><span role="columnheader">{{ t('common.status') }}</span><span role="columnheader">{{ t('diagnostics.detail') }}</span></div>
            <div v-for="(check, checkIndex) in group.checks" :key="`${check.id}-${checkIndex}`" class="assessment-row" role="row" :data-diagnostic-status="check.status || 'unknown'">
              <div role="cell"><strong :title="check.id || undefined">{{ checkLabel(check.id) }}</strong></div>
              <div class="assessment-outcome" role="cell"><NxpBadge :tone="statusTone(check.status)">{{ statusLabel(check.status) }}</NxpBadge></div>
              <div class="assessment-explanation diagnostic-check-info" role="cell">
                <p>{{ checkSummary(check) }}</p>
                <p v-if="checkDetail(check)">{{ checkDetail(check) }}</p>
                <p v-if="requiresDetail(check) && check.remediationCode" class="diagnostic-check-remediation"><strong>{{ t('diagnostics.remediation') }}：</strong>{{ diagnosticText(check.remediationCode, check.remediationArgs) }}</p>
              </div>
            </div>
          </div>
        </section>
        <NxpEmptyState v-if="!checks.length" :title="t('diagnostics.empty')" />
      </template>
    </div>
    <div class="row-actions diagnostics-actions">
      <NxpButton class="ghost" type="button" data-testid="load-diagnostics" :busy="loading" @click="loadDiagnostics">{{ t('settings.refresh_diagnostics') }}</NxpButton>
      <NxpButton class="ghost" type="button" data-testid="export-diagnostics" @click="exportDiagnostics">{{ t('settings.diagnostics.export_help') }}</NxpButton>
    </div>
  </div>
</template>
