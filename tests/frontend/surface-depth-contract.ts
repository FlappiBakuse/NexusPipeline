import { ExtensionScope, type UiElement } from '@nexuspipeline/extension-sdk';
import { EditorDraftStore } from '../../apps/web/src/app/editor-drafts';
import { SurfaceSession } from '../../apps/web/src/extensions/surface-session';
import { DateControl } from '../../apps/web/src/ui/date-time-controls';
import theme from '../../apps/web/src/styles/theme.css?inline';
import uiTheme from '../../apps/web/src/ui/theme.css?inline';
import paintedControls from '../../apps/web/src/styles/painted-controls.css?inline';
import workspace from '../../apps/web/src/styles/workspace.css?inline';

export async function surfaceDepthContract(check: (value: unknown, name: string) => void) {
  const style = document.createElement('style');
  style.textContent = [theme, uiTheme, paintedControls, workspace, ':root { --nx-motion-duration-fast: 0s; }'].join('\n');
  document.head.append(style);
  const main = document.createElement('nexus-card');
  const modal = document.createElement('nexus-modal') as UiElement<'nexus-modal'>;
  modal.surface = 'secondary';
  const scope = new ExtensionScope(), drafts = new EditorDraftStore(() => {});
  const wallpaper = new SurfaceSession(document.createElement('div'), { surfaceId: 'application.appearance', instanceId: 'theme' },
    scope, drafts.target('theme', 'theme', 'wallpaper'));
  const canvas = document.createElement('canvas').getContext('2d', { willReadFrequently: true })!;
  function color(...elements: Element[]) {
    canvas.clearRect(0, 0, 1, 1);
    for (const element of elements) {
      canvas.fillStyle = getComputedStyle(element).backgroundColor;
      canvas.fillRect(0, 0, 1, 1);
    }
    return canvas.getImageData(0, 0, 1, 1).data;
  }
  const sameColor = (first: Element, second: Element) => color(first).join() === color(second).join();
  function layer(outer: Element) {
    const raised = document.createElement('section');
    raised.dataset.surface = 'raised';
    const field = document.createElement('nexus-text-input');
    const native = document.createElement('input');
    const switches = document.createElement('nexus-switch-list');
    const empty = document.createElement('nexus-empty-state');
    raised.append(field, native, switches, empty); outer.append(raised);
    return { raised, field: field.shadowRoot!.querySelector('input')!, native, switches, empty };
  }
  document.body.append(main, modal);
  const primary = layer(main), secondary = layer(modal);
  const expandable = document.createElement('nexus-collapsible-card') as UiElement<'nexus-collapsible-card'>;
  expandable.expanded = true;
  const switches = document.createElement('nexus-switch-list');
  expandable.append(switches); document.body.append(expandable);
  const icon = document.createElement('nexus-icon-button');
  icon.className = 'danger-action';
  const actions = document.createElement('div');
  actions.className = 'entity-card-actions';
  actions.append(icon); primary.raised.append(actions);
  const controls = fieldFrames(primary.raised);
  const iconButton = icon.shadowRoot!.querySelector('button')!;
  const modalPanel = modal.shadowRoot!.querySelector('.panel')!;
  const before = document.body.dataset.theme;
  try {
    for (const mode of ['dark', 'light'] as const) {
      document.body.dataset.theme = mode;
      for (const tinted of [false, true]) {
        wallpaper.setAppearance(tinted ? { cardDark: '#17263d', cardLight: '#e9e3d5', cardOpacity: 0.72, secondaryTransparent: false } : null);
        for (const [outer, inner] of [[main, primary], [modalPanel, secondary]] as const) {
          check([inner.field, inner.native, inner.switches, inner.empty].every(element =>
            color(inner.raised, element)[3] === 0 && color(outer, inner.raised, element).join() === color(outer).join()),
            mode + ' nested cards and fields paint the outer surface color only once' + (tinted ? ' with wallpaper' : ''));
        }
        if (tinted) check(Math.abs(color(main)[3]! - 184) <= 1 && color(modalPanel)[3] === 255,
          mode + ' the page tint is 72% while the independently painted modal stays opaque');
        check(color(expandable.shadowRoot!.querySelector('.body')!, switches)[3] === 0,
          mode + ' expandable sections share their surface with the switch group');
        const frame = getComputedStyle(main);
        check(frame.boxShadow !== 'none' && [primary.switches, iconButton, primary.field, primary.native].every(element => {
          const style = getComputedStyle(element);
          return style.borderTopWidth === '1px' && style.borderTopColor === frame.borderTopColor && style.boxShadow === frame.boxShadow;
        }), mode + ' cards, native fields, custom fields and row actions share the same one-pixel frame and shadow');
        for (const [name, element] of controls.frames) {
          const frameCss = getComputedStyle(element);
          check(frameCss.borderTopWidth === '1px' && frameCss.borderTopColor === frame.borderTopColor,
            mode + ' ' + name + ' shares the card border color and width');
        }
        if (!tinted) check(!sameColor(document.body, main), mode + ' the page canvas has its own base color');
      }
    }
  } finally {
    controls.date.dispose();
    await scope.dispose(); drafts.dispose(); main.remove(); modal.remove(); expandable.remove(); style.remove();
    if (before === undefined) delete document.body.dataset.theme;
    else document.body.dataset.theme = before;
  }
}

function fieldFrames(parent: HTMLElement) {
  const frames: [string, Element][] = [];
  for (const [tag, selector] of [
    ['nexus-select', 'input'], ['nexus-number-input', 'input'], ['nexus-text-area', 'textarea'],
    ['nexus-date-range-picker', '.trigger'], ['nexus-range', '.range-track'], ['nexus-color-picker', '.value-input']
  ] as const) {
    const control = document.createElement(tag);
    parent.append(control);
    frames.push([tag, control.shadowRoot!.querySelector(selector)!]);
  }
  const path = document.createElement('nexus-path-picker') as UiElement<'nexus-path-picker'>;
  const time = document.createElement('nexus-time-picker');
  const checkbox = document.createElement('input'), radio = document.createElement('input');
  const dateHost = document.createElement('span');
  const dateRoot = dateHost.attachShadow({ mode: 'open' });
  const date = new DateControl(dateRoot, () => {});
  path.browseAllowed = true;
  path.kinds = ['folder'];
  checkbox.type = 'checkbox'; radio.type = 'radio';
  parent.append(path, time, checkbox, radio, dateHost);
  frames.push(['Path input', path.shadowRoot!.querySelector('input')!],
    ['Path action', path.shadowRoot!.querySelector('button')!],
    ['Time', time.shadowRoot!.querySelector('span')!.shadowRoot!.querySelector('.trigger')!],
    ['Checkbox', checkbox], ['Radio', radio], ['Date', dateRoot.querySelector('input')!]);
  return { frames, date };
}
