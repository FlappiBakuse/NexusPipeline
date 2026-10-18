<script setup lang="ts">
import { t } from '../../../platform/i18n';
import { hasTaskProof, structuredProofText, taskReason, type TaskProof } from '../utils/taskPresentation';
import type { TaskReport } from '../utils/taskTypes';
const props = defineProps<{ proof: TaskProof; report: TaskReport; attemptId: string }>();
function structured(id: string) { return props.report.structuredEvidence?.find(item => item.id === id); }
function host(id: string) { return props.report.hostEvidence?.find(item => item.hostEventId === id); }
function line(proof: TaskProof['evidence'][number]) {
  return props.report.evidenceLines?.find(item => item.attemptId === props.attemptId && item.sourceId === proof.sourceId
    && item.epoch === proof.epoch && item.sequence === proof.sequence)?.text || t('tasks.evidence_unavailable');
}
</script>

<template>
  <dl v-if="hasTaskProof(proof)" class="task-proof">
    <dt>{{ t('tasks.presentation.basis') }}</dt>
    <dd>
      <p v-for="id in proof.hostEvidenceRefs || []" :key="id">{{ host(id) ? taskReason('tasks.' + host(id)!.kind) : t('tasks.presentation.evidence_missing') }}</p>
      <p v-for="id in proof.structuredEvidenceRefs || []" :key="id">{{ structuredProofText(structured(id), report) }}</p>
      <pre v-for="item in proof.evidence" :key="`${item.sourceId}:${item.epoch}:${item.sequence}:${item.ruleId}`">{{ line(item) }}</pre>
    </dd>
  </dl>
</template>

<style scoped>
.task-proof { min-width: 0; margin: var(--nx-space-2) 0; display: grid; grid-template-columns: auto minmax(0, 1fr); gap: var(--nx-space-2); }
dt { color: var(--nx-color-muted); }
dd { min-width: 0; margin: 0; }
p, pre { margin: 0; }
dd > * + * { margin-top: var(--nx-space-2); }
pre { white-space: pre-wrap; overflow-wrap: anywhere; font-size: 12px; line-height: 1.6; }
</style>
