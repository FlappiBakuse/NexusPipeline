<script setup lang="ts">
import { leaveModal } from '../ui/modal-transition';
</script>

<template>
  <Transition :css="false" @leave="leaveModal"><slot /></Transition>
</template>
