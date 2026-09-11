<script setup lang="ts">
import { computed } from "vue";
import { useRoute } from "vue-router";
import UiLab from "./UiLab.vue";

// 组件状态实验室只在显式测试入口下渲染，正式导航不展示该页面。
const route = useRoute();
const enabled = computed(() => String(route.query.test || "") === "1");
</script>

<template>
  <UiLab v-if="enabled" />
</template>
