import { ExtensionScope, type UiElement } from '@nexuspipeline/extension-sdk';
import { EditorDraftStore } from '../../apps/web/src/app/editor-drafts';
import { SurfaceSession } from '../../apps/web/src/extensions/surface-session';
import theme from '../../apps/web/src/styles/theme.css?inline';
import uiTheme from '../../apps/web/src/ui/theme.css?inline';

export async function surfaceDepthContract(check: (value: unknown, name: string) => void) {
  const style = document.createElement('style');
  style.textContent = [theme, uiTheme].join('\n');
  document.head.append(style);
  const main = document.createElement('nexus-card');
  const modal = document.createElement('nexus-modal') as UiElement<'nexus-modal'>;
  modal.surface = 'secondary';
  const scope = new ExtensionScope(), drafts = new EditorDraftStore(() => {});
  const wallpaper = new SurfaceSession(document.createElement('div'), { surfaceId: 'application.appearance', instanceId: 'theme' },
    scope, drafts.target('theme', 'theme', 'wallpaper'));
  const canvas = document.createElement('canvas').getContext('2d', { willReadFrequently: true })!;
  function color(element: Element) {
    canvas.clearRect(0, 0, 1, 1);
    canvas.fillStyle = getComputedStyle(element).backgroundColor;
    canvas.fillRect(0, 0, 1, 1);
    const [red, green, blue, alpha] = canvas.getImageData(0, 0, 1, 1).data;
    return { lightness: red! * 0.2126 + green! * 0.7152 + blue! * 0.0722, alpha };
  }
  function layer(outer: Element) {
    const raised = document.createElement('section');
    raised.dataset.surface = 'raised';
    const field = document.createElement('nexus-text-input');
    const switches = document.createElement('nexus-switch-list');
    const empty = document.createElement('nexus-empty-state');
    raised.append(field, switches, empty); outer.append(raised);
    return { raised, field: field.shadowRoot!.querySelector('input')!, switches, empty };
  }
  document.body.append(main, modal);
  const primary = layer(main), secondary = layer(modal);
  const expandable = document.createElement('nexus-collapsible-card') as UiElement<'nexus-collapsible-card'>;
  expandable.expanded = true;
  const switches = document.createElement('nexus-switch-list');
  expandable.append(switches); document.body.append(expandable);
  const binding = document.createElement('article');
  binding.dataset.surface = 'collapsible'; modal.append(binding);
  const policy = layer(binding);
  const modalPanel = modal.shadowRoot!.querySelector('.panel')!;
  const before = document.body.dataset.theme;
  try {
    for (const mode of ['dark', 'light'] as const) {
      document.body.dataset.theme = mode;
      for (const tinted of [false, true]) {
        wallpaper.setAppearance(tinted ? { cardDark: '#17263d', cardLight: '#e9e3d5', cardOpacity: 0.72, secondaryTransparent: false } : null);
        for (const [outer, inner] of [[main, primary], [modalPanel, secondary]] as const) {
          const base = color(outer), middle = color(inner.raised), control = color(inner.field);
          check(base.lightness < middle.lightness && Math.abs(base.lightness - control.lightness) < 1 && base.alpha === control.alpha,
            mode + ' nested public fields use deep, light and deep surfaces' + (tinted ? ' with wallpaper' : ''));
          check([inner.switches, inner.empty].every(element => color(element).lightness === control.lightness),
            mode + ' switch groups and empty states follow the inner control color');
        }
        if (tinted) check(color(main).alpha! < 255 && color(modalPanel).alpha === 255 && color(secondary.raised).alpha === 255,
          mode + ' modal layers stay opaque when wallpaper applies only to the main page');
        check(color(expandable.shadowRoot!.querySelector('.body')!).alpha === 0 && color(expandable).lightness < color(switches).lightness,
          mode + ' expandable header and body share one surface while their switch group is lighter');
        check(color(modalPanel).lightness < color(binding).lightness && color(binding).lightness > color(policy.raised).lightness &&
          Math.abs(color(binding).lightness - color(policy.field).lightness) < 1,
          mode + ' binding, nested policy, and input alternate light, deep, and light inside a modal');
        check(color(policy.switches).lightness === color(policy.field).lightness,
          mode + ' an account binding and its nested switch group alternate across all three levels');
      }
    }
  } finally {
    await scope.dispose(); drafts.dispose(); main.remove(); modal.remove(); expandable.remove(); style.remove();
    if (before === undefined) delete document.body.dataset.theme;
    else document.body.dataset.theme = before;
  }
}
