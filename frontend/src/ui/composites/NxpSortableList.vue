<script setup lang="ts">
import { computed, useAttrs } from "vue";
import { vSortable, type SortableOptions } from "../sortable";

defineOptions({ inheritAttrs: false });

const props = withDefaults(defineProps<{
  axis?: "y" | "both";
  disabled?: boolean;
  tag?: string;
  transitionName?: string;
  canDrag?: SortableOptions["canDrag"];
}>(), {
  axis: "y",
  disabled: false,
  tag: "div",
  transitionName: "",
});

const emit = defineEmits<{ reorder: [ids: string[], movedId: string] }>();
const attrs = useAttrs();
const options = computed<SortableOptions>(() => ({
  axis: props.axis,
  disabled: props.disabled,
  canDrag: props.canDrag,
  onDrop: (ids, movedId) => emit("reorder", ids, movedId),
}));
</script>

<template>
  <TransitionGroup
    v-if="props.transitionName"
    v-bind="attrs"
    :name="props.transitionName"
    :tag="props.tag"
    v-sortable="options"
  >
    <slot />
  </TransitionGroup>
  <component
    :is="props.tag"
    v-else
    v-bind="attrs"
    v-sortable="options"
  >
    <slot />
  </component>
</template>

<style>
:host { display: block; min-width: 0; }
</style>
