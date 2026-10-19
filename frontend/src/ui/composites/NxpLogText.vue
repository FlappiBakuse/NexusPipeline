<script setup lang="ts">
import { computed } from 'vue';

const props = defineProps<{ text: string; level?: string }>();
const lines = computed(() => {
  const level = props.level?.trim() || '';
  const errorLevel = /^(?:error|err|fatal|ftl|critical|crit|crt)$/i.test(level);
  const warnLevel = /^(?:warning|warn|wrn)$/i.test(level);
  let previousTone = errorLevel ? 'error'
    : warnLevel ? 'warn'
    : /^(?:debug|dbg|trace|trc)$/i.test(level) ? 'debug' : 'normal';
  return (props.text.match(/[^\n]*\n|[^\n]+$/g) || []).map(text => {
    // Some upstream formatters put the message and exception on separate lines.
    const readable = text.replace(/\u001b\[[0-9;]*m/g, '');
    const marker = readable.match(/\b(?:warning|warn|wrn|error|err|fatal|ftl|critical|crit|crt|information|info|inf|debug|dbg|trace|trc)\b/i)?.[0] || '';
    let tone = errorLevel || /\b(?:error|err|fatal|ftl|critical|crit|crt)\b/i.test(readable) ? 'error'
      : warnLevel || /\b(?:warning|warn|wrn)\b/i.test(readable) ? 'warn'
      : /\b(?:debug|dbg|trace|trc)\b/i.test(marker) ? 'debug'
      : marker ? 'normal' : previousTone;
    if (/^\s*Traceback \(most recent call last\):/.test(readable)) tone = 'error';
    previousTone = tone;
    return { text, tone };
  });
});
</script>

<template><span v-for="(line, index) in lines" :key="index" class="nxp-log-line" :class="`nxp-log-${line.tone}`">{{ line.text }}</span></template>

<style scoped>
.nxp-log-warn { color: var(--warn); }
.nxp-log-error { color: var(--bad); }
.nxp-log-debug { color: var(--muted); }
</style>
