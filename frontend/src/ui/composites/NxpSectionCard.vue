<script setup lang="ts">
/** Nexus section surface：一级卡片使用 `is-primary`，二级分组使用 `is-secondary`。
 *  header 承载标题、说明与右侧 actions；不带 header 时退化为纯 surface。 */
const props = withDefaults(defineProps<{
  title?: string;
  description?: string;
  variant?: "primary" | "secondary";
}>(), {
  title: "",
  description: "",
  variant: "primary",
});
</script>

<template>
  <section class="nxp-section-card" :class="[`is-${props.variant}`, { 'has-header': Boolean(props.title || props.description || $slots.header || $slots.actions) }]">
    <header v-if="props.title || props.description || $slots.header || $slots.actions" class="nxp-section-card-header">
      <slot name="header">
        <div class="nxp-section-card-copy">
          <h3 v-if="props.title" class="nxp-section-card-title">{{ props.title }}</h3>
          <p v-if="props.description" class="muted nxp-section-card-description">{{ props.description }}</p>
          <slot name="description" />
        </div>
      </slot>
      <div v-if="$slots.actions" class="nxp-section-card-actions"><slot name="actions" /></div>
    </header>
    <div class="nxp-section-card-body"><slot /></div>
  </section>
</template>

<style>
:host { display: block; min-width: 0; }
.nxp-section-card { min-width: 0; overflow: hidden; border: 1px solid var(--content-card-border); border-radius: var(--radius-lg); background: var(--content-card); color: var(--text); }
.nxp-section-card.is-secondary { border-color: var(--border); background: var(--content-card-soft); }
.nxp-section-card-header { display: flex; min-width: 0; align-items: flex-start; justify-content: space-between; gap: var(--space-4); padding: var(--space-4) var(--space-5); }
.nxp-section-card.has-header .nxp-section-card-body { padding: 0 var(--space-5) var(--space-5); }
.nxp-section-card-header + .nxp-section-card-body { padding-top: var(--space-4); }
.nxp-section-card-copy { display: grid; min-width: 0; gap: 4px; }
.nxp-section-card-title { margin: 0; font-size: 15px; line-height: 1.45; }
.nxp-section-card-description { margin: 0; line-height: 1.5; }
.nxp-section-card-actions { display: flex; flex: 0 0 auto; align-items: center; gap: var(--action-gap); }
</style>
