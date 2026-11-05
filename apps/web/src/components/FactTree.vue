<script setup lang="ts">
import { fieldLabel } from '../app/labels';
defineProps<{ value: unknown; label?: string }>();
</script>
<template>
  <dl v-if="value && typeof value === 'object' && !Array.isArray(value)" class="fact-tree">
    <template v-for="(item, key) in value" :key="key">
      <dt>{{ fieldLabel(String(key)) }}</dt>
      <dd><FactTree :value="item" /></dd>
    </template>
  </dl>
  <ul v-else-if="Array.isArray(value)" class="fact-list">
    <li v-for="(item, index) in value" :key="index"><FactTree :value="item" /></li>
  </ul>
  <span v-else>{{ value === null || value === undefined ? '—' : String(value) }}</span>
</template>
