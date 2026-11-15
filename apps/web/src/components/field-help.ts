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
    tooltip.style.cssText = 'position:absolute;width:0;height:0';
    tooltip.anchor = element;
    element.after(tooltip);
    owners.set(element, tooltip);
  }
  tooltip.content = text;
}

export const fieldHelp: ObjectDirective<HTMLElement, string | undefined> = {
  mounted: (element, binding) => update(element, binding.value),
  updated: (element, binding) => update(element, binding.value),
  beforeUnmount: (element) => update(element, undefined)
};
