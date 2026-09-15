<script setup lang="ts">
import { computed, ref, useAttrs, watch } from "vue";
import NxpIcon from "./NxpIcon.vue";

defineOptions({ inheritAttrs: false });

const attrs = useAttrs();

const props = withDefaults(defineProps<{
  id?: string;
  modelValue?: string;
  placeholder?: string;
  disabled?: boolean;
  type?: string;
  maxlength?: number | string;
  readonly?: boolean;
  autocomplete?: string;
  ariaLabel?: string;
  showPasswordToggle?: boolean;
  showPasswordLabel?: string;
  hidePasswordLabel?: string;
}>(), {
  id: undefined,
  modelValue: "",
  placeholder: "",
  disabled: false,
  type: "text",
  maxlength: undefined,
  readonly: false,
  autocomplete: undefined,
  ariaLabel: undefined,
  showPasswordToggle: false,
  showPasswordLabel: "Show password",
  hidePasswordLabel: "Hide password",
});
const emit = defineEmits<{ "update:modelValue": [value: string]; change: [value: string] }>();
const input = ref<HTMLInputElement | null>(null);
const passwordVisible = ref(false);

const inputAttrs = computed(() => {
  const {
    class: _class,
    style: _style,
    id: _id,
    "aria-label": _ariaLabel,
    ...rest
  } = attrs;
  return rest;
});
const inputClass = computed(() => attrs.class);
const inputStyle = computed(() => attrs.style);
const inputId = computed(() => props.id || (typeof attrs.id === "string" ? attrs.id : undefined));
const ariaLabel = computed(() => props.ariaLabel || (typeof attrs["aria-label"] === "string" ? attrs["aria-label"] : undefined));
const baseType = computed(() => String(props.type || "text"));
const isPasswordInput = computed(() => baseType.value.toLowerCase() === "password");
const passwordToggleEnabled = computed(() => props.showPasswordToggle && isPasswordInput.value);
const hasPasswordValue = computed(() => String(props.modelValue || "").length > 0);
const passwordToggleVisible = computed(() => passwordToggleEnabled.value && !props.disabled && !props.readonly && hasPasswordValue.value);
const inputType = computed(() => passwordToggleEnabled.value && passwordVisible.value ? "text" : baseType.value);

watch(
  () => [props.type, props.modelValue, props.disabled, props.readonly, props.showPasswordToggle],
  () => {
    if (!passwordToggleEnabled.value || !hasPasswordValue.value || props.disabled || props.readonly) passwordVisible.value = false;
  },
);

function update(event: Event) { const value = (event.target as HTMLInputElement).value; emit("update:modelValue", value); }
function change(event: Event) { emit("change", (event.target as HTMLInputElement).value); }
function togglePasswordVisibility() {
  if (!passwordToggleVisible.value) return;
  passwordVisible.value = !passwordVisible.value;
  input.value?.focus();
}
defineExpose({ focus: () => input.value?.focus() });
</script>

<template>
  <div v-if="passwordToggleEnabled" class="nxp-password-input">
    <input ref="input" v-bind="inputAttrs" :class="[inputClass, 'nxp-input']" :style="inputStyle" :id="inputId" :type="inputType" :value="props.modelValue" :placeholder="props.placeholder" :disabled="props.disabled" :maxlength="props.maxlength" :readonly="props.readonly" :autocomplete="props.autocomplete" :aria-label="ariaLabel" @input.stop="update" @change.stop="change" />
    <button
      v-if="passwordToggleVisible"
      class="nxp-password-toggle"
      type="button"
      :aria-label="passwordVisible ? props.hidePasswordLabel : props.showPasswordLabel"
      :aria-pressed="passwordVisible"
      @mousedown.prevent
      @click="togglePasswordVisibility"
    >
      <NxpIcon :name="passwordVisible ? 'eyeOff' : 'eye'" />
    </button>
  </div>
  <input v-else ref="input" v-bind="inputAttrs" :class="[inputClass, 'nxp-input']" :style="inputStyle" :id="inputId" :type="inputType" :value="props.modelValue" :placeholder="props.placeholder" :disabled="props.disabled" :maxlength="props.maxlength" :readonly="props.readonly" :autocomplete="props.autocomplete" :aria-label="ariaLabel" @input.stop="update" @change.stop="change" />
</template>

<style>
.nxp-input { width: 100%; min-height: var(--nx-control-height); border: 1px solid var(--nx-color-border); border-radius: var(--nx-radius-sm); padding: 0 var(--nx-space-3); background: var(--input-bg, transparent); color: var(--nx-color-text); font: inherit; }
.nxp-input:focus { border-color: var(--nx-color-accent); box-shadow: var(--nx-focus-ring); outline: 0; }
.nxp-input:disabled { opacity: .55; }
.nxp-password-input { position: relative; display: block; width: 100%; min-width: 0; }
.nxp-password-input > .nxp-input { padding-right: calc(var(--nx-space-3) + 32px); }
.nxp-password-input > .nxp-input::-ms-reveal { display: none; }
.nxp-password-toggle { position: absolute; top: 50%; right: 4px; display: inline-flex; width: 32px; height: 32px; align-items: center; justify-content: center; padding: 0; border: 0; border-radius: var(--nx-radius-sm); transform: translateY(-50%); background: transparent; color: var(--nx-color-muted); cursor: pointer; }
.nxp-password-toggle:hover { background: var(--content-control-hover, var(--nx-color-surface)); color: var(--nx-color-text); }
.nxp-password-toggle:focus-visible { outline: 2px solid var(--nx-color-accent); outline-offset: 1px; }
.nxp-password-toggle .nxp-icon { width: 17px; height: 17px; }
</style>
