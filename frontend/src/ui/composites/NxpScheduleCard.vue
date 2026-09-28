<script setup lang="ts">
import NxpButton from "../primitives/NxpButton.vue";
import NxpDragHandle from "./NxpDragHandle.vue";
import { useSlotPresence } from "../slots";

const hasSlot = useSlotPresence();

const props = withDefaults(defineProps<{
  itemId?: string;
  panelId?: string;
  summaryLabel?: string;
  summaryMeta?: string;
  daysLabel?: string;
  daysAriaLabel?: string;
  timeLabel?: string;
  expanded?: boolean;
  dragLabel?: string;
  dragTitle?: string;
}>(), {
  itemId: "",
  panelId: "",
  summaryLabel: "",
  summaryMeta: "",
  daysLabel: "",
  daysAriaLabel: "",
  timeLabel: "",
  expanded: false,
  dragLabel: "Reorder",
  dragTitle: "",
});

const emit = defineEmits<{ toggle: [expanded: boolean] }>();

function toggle() {
  emit("toggle", !props.expanded);
}
</script>

<template>
  <article
    class="nxp-schedule-card"
    :class="{ 'nxp-schedule-card-open': props.expanded }"
    :data-dnd-id="props.itemId || undefined"
  >
    <div class="nxp-schedule-head">
      <NxpDragHandle class="nxp-schedule-drag-handle" :label="props.dragLabel" :title="props.dragTitle" />
      <NxpButton
        class="nxp-schedule-summary"
        type="button"
        :aria-expanded="props.expanded"
        :aria-controls="props.panelId || undefined"
        @click="toggle"
      >
        <span class="nxp-schedule-summary-main">
          <strong>{{ props.summaryLabel }}</strong>
          <span>{{ props.summaryMeta }}</span>
        </span>
        <span class="nxp-schedule-chevron" aria-hidden="true">{{ props.expanded ? "⌄" : "›" }}</span>
      </NxpButton>
    </div>
    <div v-if="props.expanded" :id="props.panelId || undefined" class="nxp-schedule-details">
      <div class="nxp-schedule-layout">
        <div class="nxp-schedule-days">
          <span class="nxp-schedule-field-label">{{ props.daysLabel }}</span>
          <div class="nxp-schedule-day-buttons" role="group" :aria-label="props.daysAriaLabel || props.daysLabel || undefined">
            <slot name="days" />
          </div>
        </div>
        <div class="nxp-schedule-time">
          <span class="nxp-schedule-field-label">{{ props.timeLabel }}</span>
          <div class="nxp-schedule-time-control">
            <slot name="time" />
          </div>
        </div>
      </div>
      <div v-if="hasSlot('actions')" class="nxp-schedule-actions">
        <slot name="actions" />
      </div>
    </div>
  </article>
</template>

<style>
:host { display: block; min-width: 0; }

.nxp-schedule-card {
  display: block;
  min-width: 0;
  padding: 0;
  border-bottom: 1px solid var(--nx-color-border, var(--border));
  border-radius: var(--nx-radius-md, var(--radius-md));
  background: transparent;
  color: var(--nx-color-text, var(--text));
}
.nxp-schedule-card:last-child { border-bottom: 0; }
.nxp-schedule-card:hover { background: transparent; }
.nxp-schedule-card.dnd-dragging { opacity: .55; }
.nxp-schedule-card.dnd-drop-before { box-shadow: inset 0 2px 0 var(--nx-color-accent, var(--accent)); }

.nxp-schedule-head {
  display: flex;
  min-width: 0;
  align-items: flex-start;
  gap: 10px;
  padding: 13px 0;
}
.nxp-schedule-head > .nxp-schedule-drag-handle {
  flex: 0 0 auto;
  align-self: flex-start;
  margin: 2px 0 0;
}

.nxp-schedule-summary {
  display: flex;
  width: 100%;
  min-width: 0;
  min-height: 40px;
  flex: 1 1 auto;
  align-items: center;
  justify-content: space-between;
  gap: var(--nx-space-3, var(--space-3));
  padding: 0;
  border: 0;
  border-radius: 0;
  background: transparent;
  color: inherit;
  text-align: left;
  box-shadow: none;
}
.nxp-schedule-summary:hover { background: transparent; }
.nxp-schedule-summary-main {
  display: flex;
  min-width: 0;
  align-items: center;
  gap: var(--nx-space-2, var(--space-2));
}
.nxp-schedule-summary-main strong,
.nxp-schedule-summary-main > span {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.nxp-schedule-summary-main strong { font-size: 13px; }
.nxp-schedule-summary-main > span { color: var(--nx-color-muted, var(--muted)); }
.nxp-schedule-chevron {
  flex: 0 0 auto;
  color: var(--nx-color-muted, var(--muted));
  font-size: 12px;
}

.nxp-schedule-details {
  display: grid;
  min-width: 0;
  gap: var(--nx-space-3, var(--space-3));
  padding: 0 0 14px 34px;
}
.nxp-schedule-layout {
  display: grid;
  min-width: 0;
  grid-template-columns: minmax(0, 3fr) minmax(150px, 1fr);
  align-items: stretch;
  gap: var(--nx-space-4, var(--space-4));
}
.nxp-schedule-days,
.nxp-schedule-time {
  display: flex;
  min-width: 0;
  flex-direction: column;
}
.nxp-schedule-field-label {
  display: block;
  margin-bottom: var(--nx-space-2, var(--space-2));
  color: var(--nx-color-muted, var(--muted));
  font-size: 12px;
}
.nxp-schedule-day-buttons {
  display: grid;
  min-width: 0;
  grid-template-columns: repeat(7, minmax(0, 1fr));
  gap: var(--nx-space-1, var(--space-1));
}
.nxp-schedule-day-buttons > button,
.nxp-schedule-day-buttons > nxp-button {
  display: inline-flex;
  width: 100%;
  min-width: 0;
  min-height: 40px;
  margin-left: 0;
}
.nxp-schedule-day-buttons > button {
  padding: 0 6px;
  border: 1px solid var(--content-control-border, var(--nx-color-border, var(--border)));
  border-radius: var(--nx-radius-md, var(--radius-md));
  background: var(--content-control, var(--nx-color-surface));
  color: var(--nx-color-muted, var(--muted));
}
.nxp-schedule-day-buttons > nxp-button {
  border: 0;
  border-radius: 0;
  background: transparent;
  color: inherit;
  --nx-button-width: 100%;
  --nx-button-min-width: 0;
  --nx-button-min-height: 40px;
  --nx-button-padding: 0 6px;
  --nx-button-radius: var(--nx-radius-md, var(--radius-md));
  --nx-button-border-color: var(--content-control-border, var(--nx-color-border, var(--border)));
  --nx-button-background: var(--content-control, var(--nx-color-surface));
  --nx-button-color: var(--nx-color-muted, var(--muted));
  --nx-button-hover-background: var(--content-control-hover, var(--nx-color-surface));
}
.nxp-schedule-day-buttons > nxp-button:hover {
  --nx-button-background: var(--content-control-hover, var(--nx-color-surface));
}
.nxp-schedule-day-buttons > nxp-button[aria-pressed="true"] {
  --nx-button-border-color: var(--nx-color-accent, var(--accent));
  --nx-button-background: var(--nx-color-accent-soft, var(--accent-soft));
  --nx-button-color: var(--nx-color-accent, var(--accent));
}
.nxp-schedule-day-buttons > button:hover {
  background: var(--content-control-hover, var(--nx-color-surface));
}
.nxp-schedule-day-buttons > button[aria-pressed="true"] {
  border-color: var(--nx-color-accent, var(--accent));
  background: var(--nx-color-accent-soft, var(--accent-soft));
  color: var(--nx-color-accent, var(--accent));
}
.nxp-schedule-day-buttons > button:focus-visible,
.nxp-schedule-day-buttons > button:focus-within,
.nxp-schedule-day-buttons > nxp-button:focus-within {
  outline: none;
  box-shadow: var(--nx-focus-ring, var(--focus));
}
.nxp-schedule-time-control,
.nxp-schedule-time-control > * {
  display: block;
  width: 100%;
  min-width: 0;
}
.nxp-schedule-actions {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 10px;
  margin-top: 14px;
  --nx-switch-control-width: 56px;
  --nx-switch-control-height: 40px;
  --nx-switch-track-width: 52px;
  --nx-switch-track-height: 30px;
}

@media (max-width: 820px) {
  .nxp-schedule-layout { grid-template-columns: minmax(0, 1fr); gap: var(--nx-space-1, var(--space-1)); }
}

@media (max-width: 480px) {
  .nxp-schedule-details { padding-left: 0; }
  .nxp-schedule-day-buttons { grid-template-columns: repeat(4, minmax(0, 1fr)); }
}
</style>
