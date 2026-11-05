import { NexusButtonElement } from './button-element.js';
import { NexusCardElement } from './card-element.js';
import { NexusModalElement } from './modal-element.js';
import { NexusSwitchElement } from './switch-element.js';
import { NexusTabsElement } from './tabs-element.js';
import { TextInput } from './text-input-element.js';
import {
  TextArea,
  NumberInput,
  NexusRangeElement,
  FilePicker,
  ColorPicker,
  TimePicker
} from './input-elements.js';
import { DateRangePicker, PathPicker } from './picker-elements.js';
import { IconButton, NexusMenuElement, NexusSelectElement, Pager } from './action-elements.js';
import { DragHandle, SortableList, ConfirmDialog, DialogPopover } from './dialog-elements.js';
import {
  NexusFieldElement,
  SectionCard,
  CollapsibleCard,
  ActionGroup,
  EntityRow,
  ScheduleCard,
  PageHeader,
  SwitchSetting,
  SwitchList
} from './structure-elements.js';
import {
  NexusBadgeElement,
  OverflowText,
  NexusSpinnerElement,
  EmptyState,
  NexusTooltipElement,
  NexusToastElement,
  NexusIconElement,
  ScrollArea,
  LoadingState
} from './feedback-elements.js';

const elements = {
  'nexus-button': NexusButtonElement,
  'nexus-card': NexusCardElement,
  'nexus-modal': NexusModalElement,
  'nexus-switch': NexusSwitchElement,
  'nexus-tabs': NexusTabsElement,
  'nexus-text-input': TextInput,
  'nexus-text-area': TextArea,
  'nexus-number-input': NumberInput,
  'nexus-range': NexusRangeElement,
  'nexus-file-picker': FilePicker,
  'nexus-color-picker': ColorPicker,
  'nexus-time-picker': TimePicker,
  'nexus-date-range-picker': DateRangePicker,
  'nexus-path-picker': PathPicker,
  'nexus-icon-button': IconButton,
  'nexus-menu': NexusMenuElement,
  'nexus-select': NexusSelectElement,
  'nexus-pager': Pager,
  'nexus-drag-handle': DragHandle,
  'nexus-sortable-list': SortableList,
  'nexus-confirm-dialog': ConfirmDialog,
  'nexus-dialog-popover': DialogPopover,
  'nexus-field': NexusFieldElement,
  'nexus-section-card': SectionCard,
  'nexus-collapsible-card': CollapsibleCard,
  'nexus-action-group': ActionGroup,
  'nexus-entity-row': EntityRow,
  'nexus-schedule-card': ScheduleCard,
  'nexus-page-header': PageHeader,
  'nexus-switch-setting': SwitchSetting,
  'nexus-switch-list': SwitchList,
  'nexus-badge': NexusBadgeElement,
  'nexus-overflow-text': OverflowText,
  'nexus-spinner': NexusSpinnerElement,
  'nexus-empty-state': EmptyState,
  'nexus-tooltip': NexusTooltipElement,
  'nexus-toast': NexusToastElement,
  'nexus-icon': NexusIconElement,
  'nexus-scroll-area': ScrollArea,
  'nexus-loading-state': LoadingState
};

export function registerElements(registry: CustomElementRegistry = customElements): void {
  for (const [name, constructor] of Object.entries(elements)) {
    const existing = registry.get(name);
    if (existing && existing !== constructor)
      throw new Error('Public element already has another owner: ' + name);
    if (!existing) registry.define(name, constructor);
  }
}
