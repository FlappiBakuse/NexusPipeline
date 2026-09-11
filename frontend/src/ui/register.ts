import { defineCustomElement } from "vue";
import Button from "./primitives/NxpButton.vue";
import Badge from "./primitives/NxpBadge.vue";
import Card from "./primitives/NxpCard.vue";
import Field from "./primitives/NxpField.vue";
import TextInput from "./primitives/NxpTextInput.vue";
import TextArea from "./primitives/NxpTextArea.vue";
import Switch from "./primitives/NxpSwitch.vue";
import Select from "./primitives/NxpSelect.vue";
import NumberInput from "./primitives/NxpNumberInput.vue";
import Range from "./primitives/NxpRange.vue";
import FilePicker from "./primitives/NxpFilePicker.vue";
import ColorPicker from "./primitives/NxpColorPicker.vue";
import TimePicker from "./primitives/NxpTimePicker.vue";
import Spinner from "./primitives/NxpSpinner.vue";
import EmptyState from "./primitives/NxpEmptyState.vue";
import IconButton from "./primitives/NxpIconButton.vue";
import PathPicker from "./primitives/NxpPathPicker.vue";
import Menu from "./primitives/NxpMenu.vue";
import Tooltip from "./primitives/NxpTooltip.vue";
import Pager from "./primitives/NxpPager.vue";
import Modal from "./primitives/NxpModal.vue";
import Toast from "./primitives/NxpToast.vue";
import Icon from "./primitives/NxpIcon.vue";
import SwitchSetting from "./composites/NxpSwitchSetting.vue";
import LoadingState from "./composites/NxpLoadingState.vue";

export const NEXUS_PUBLIC_ELEMENTS = {
  "nxp-button": Button,
  "nxp-badge": Badge,
  "nxp-card": Card,
  "nxp-field": Field,
  "nxp-text-input": TextInput,
  "nxp-text-area": TextArea,
  "nxp-switch": Switch,
  "nxp-select": Select,
  "nxp-number-input": NumberInput,
  "nxp-range": Range,
  "nxp-file-picker": FilePicker,
  "nxp-color-picker": ColorPicker,
  "nxp-time-picker": TimePicker,
  "nxp-spinner": Spinner,
  "nxp-empty-state": EmptyState,
  "nxp-icon-button": IconButton,
  "nxp-path-picker": PathPicker,
  "nxp-menu": Menu,
  "nxp-tooltip": Tooltip,
  "nxp-pager": Pager,
  "nxp-modal": Modal,
  "nxp-toast": Toast,
  "nxp-icon": Icon,
  "nxp-switch-setting": SwitchSetting,
  "nxp-loading-state": LoadingState,
} as const;

export function registerNexusElements() {
  if (typeof customElements === "undefined") return;
  Object.entries(NEXUS_PUBLIC_ELEMENTS).forEach(([name, component]) => {
    if (!customElements.get(name)) customElements.define(name, defineCustomElement(component));
  });
}
