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
import ScrollArea from "./primitives/NxpScrollArea.vue";
import SwitchSetting from "./composites/NxpSwitchSetting.vue";
import SwitchList from "./composites/NxpSwitchList.vue";
import LoadingState from "./composites/NxpLoadingState.vue";
import SectionCard from "./composites/NxpSectionCard.vue";
import CollapsibleCard from "./composites/NxpCollapsibleCard.vue";

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
  "nxp-scroll-area": ScrollArea,
  "nxp-switch-setting": SwitchSetting,
  "nxp-switch-list": SwitchList,
  "nxp-loading-state": LoadingState,
  "nxp-section-card": SectionCard,
  "nxp-collapsible-card": CollapsibleCard,
} as const;

const nativeTextContent = Object.getOwnPropertyDescriptor(Node.prototype, "textContent") as {
  get(this: Node): string | null;
  set(this: Node, value: string | null): void;
};

/**
 * 公开元素的 light DOM 子节点就是组件插槽内容；父级 Vue 应用会把自定义元素当作普通元素打补丁，
 * 动态文本变化时直接用 `textContent` 覆盖子节点，抹掉元素已经渲染出的结构与样式。
 * 这里把外部写入的文本重新作为默认插槽内容挂载，使 `<nxp-badge>{{ text }}</nxp-badge>` 这类用法
 * 在任意次重渲染后仍然保持元素自身的 DOM 与交互。
 */
function defineResilientElement(component: Parameters<typeof defineCustomElement>[0]) {
  const Base = defineCustomElement(component, { shadowRoot: false }) as unknown as {
    new (): HTMLElement & Record<string, unknown>;
    prototype: HTMLElement;
  };
  return class extends Base {
    get textContent(): string {
      return nativeTextContent.get.call(this) ?? "";
    }

    set textContent(value: string | null) {
      const element = this as unknown as Record<string, any>;
      const hasChildren = element.firstChild !== null;
      // 元素尚未接管渲染（首次挂载前的外部预置内容）时按普通元素处理。
      if (!element._instance || typeof element._mount !== "function" || !hasChildren) {
        nativeTextContent.set.call(this, value);
        return;
      }
      try {
        element._app?.unmount?.();
      } catch {
        // 内部渲染卸载异常不应阻断重建。
      }
      element._app = null;
      element._instance = null;
      // 丢弃上一次渲染的 vnode：外部写入已经把渲染结果从文档中移除。
      element._vnode = null;
      element._slots = value == null || value === ""
        ? {}
        : { default: [document.createTextNode(String(value))] };
      while (element.firstChild) element.removeChild(element.firstChild);
      element._mount(element._def);
    }
  };
}

export function registerNexusElements() {
  if (typeof customElements === "undefined") return;
  Object.entries(NEXUS_PUBLIC_ELEMENTS).forEach(([name, component]) => {
    if (!customElements.get(name)) {
      // Public elements share the host page's design-token and legacy layout CSS.
      // Keeping them in light DOM preserves that contract for official plugins.
      customElements.define(name, defineResilientElement(component));
    }
  });
}
