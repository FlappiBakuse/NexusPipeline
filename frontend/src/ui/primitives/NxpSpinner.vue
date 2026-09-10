<template><span class="nxp-spinner" role="status" aria-label="加载中" /></template>

<style>
.nxp-spinner { display: inline-block; width: 20px; height: 20px; border: 2px solid color-mix(in srgb, var(--nx-color-accent) 25%, transparent); border-top-color: var(--nx-color-accent); border-radius: 50%; animation: nxp-spin .8s linear infinite; }
@keyframes nxp-spin { to { transform: rotate(360deg); } }
</style>
