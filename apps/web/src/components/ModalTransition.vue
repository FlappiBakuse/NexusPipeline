<script setup lang="ts">
import { leaveModal } from '../ui/modal-surface';
</script>

<template>
  <Transition :css="false" @leave="leaveModal"><slot /></Transition>
</template>
