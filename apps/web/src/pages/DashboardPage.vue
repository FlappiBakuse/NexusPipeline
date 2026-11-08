<script setup lang="ts">
import type { AppStore } from '../app/store';
import DashboardMain from './DashboardMain.vue';
import type { SurfaceContribution } from '@nexuspipeline/extension-sdk';
defineProps<{ store: AppStore; visible: boolean; overviewCards?: readonly { extensionId: string; id: string; dashboard?: SurfaceContribution['dashboard'] }[] }>();
</script>
<template><DashboardMain :store="store" :visible="visible" :overview-cards="overviewCards" /></template>
