<script setup lang="ts">
import { ref } from "vue";
import { useSurface } from "../surface";
const root = ref<HTMLElement | null>(null);
useSurface(root);
const props = withDefaults(defineProps<{ tone?: "default" | "secondary"; as?: "section" | "article"; unstyled?: boolean }>(), { tone: "default", as: "section", unstyled: false });
</script>

<template><component :is="props.as" :ref="(element: any) => root = props.unstyled ? null : element" class="nxp-card" :class="[`is-${props.tone}`, { 'is-unstyled': props.unstyled }]" ><slot /></component></template>

<style>
:host { display: block; min-width: 0; }
:where(.nxp-card:not(.is-unstyled)) { min-width: 0; border: 1px solid var(--nx-color-border); border-radius: var(--nx-radius-lg); padding: var(--nx-space-5); background: var(--nx-color-surface); color: var(--nx-color-text); }
:where(.nxp-card.is-secondary:not(.is-unstyled)) { background: var(--secondary-surface, var(--nx-color-surface)); }
</style>
