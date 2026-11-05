<script setup lang="ts">
defineProps<{ visible: boolean }>();
</script>
<template>
  <Teleport to="#page-actions">
    <div v-if="visible" class="page-actions"><slot /></div>
  </Teleport>
</template>
