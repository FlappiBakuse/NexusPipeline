<script setup lang="ts">
import type { AppStore } from '../app/store';
import DashboardMain from './DashboardMain.vue';
defineProps<{ store: AppStore; visible: boolean; overviewAvailable?: boolean }>();
</script>
<template><DashboardMain :store="store" :visible="visible" :overview-available="overviewAvailable" /></template>
