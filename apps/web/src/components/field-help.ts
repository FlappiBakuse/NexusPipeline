import type { ObjectDirective } from 'vue';
import type { UiElement } from '@nexuspipeline/extension-sdk';

const owners = new WeakMap<HTMLElement, UiElement<'nexus-tooltip'>>();

function update(element: HTMLElement, text: string | undefined): void {
  let tooltip = owners.get(element);
  if (!text) {
    tooltip?.remove();
    owners.delete(element);
    return;
  }
  if (!tooltip) {
    tooltip = document.createElement('nexus-tooltip') as UiElement<'nexus-tooltip'>;
    tooltip.anchor = element;
    const caption = element.querySelector(':scope > .field-help-caption');
    if (caption) caption.append(tooltip);
    else element.after(tooltip);
    owners.set(element, tooltip);
  }
  tooltip.content = text;
}

export const fieldHelp: ObjectDirective<HTMLElement, string | undefined> = {
  mounted: (element, binding) => update(element, binding.value),
  updated: (element, binding) => update(element, binding.value),
  beforeUnmount: (element) => update(element, undefined)
};
