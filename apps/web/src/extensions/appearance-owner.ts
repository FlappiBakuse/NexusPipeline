import type { AppearanceOverrides } from '@nexuspipeline/extension-sdk';
import type { SurfaceSession } from './surface-session.js';

const variables = [
  '--accent',
  '--accent-strong',
  '--accent-soft',
  '--panel',
  '--panel-soft',
  '--panel-hover'
] as const;
const allowed = new Set([
  'accent',
  'accentStrong',
  'cardDark',
  'cardLight',
  'cardOpacity',
  'secondaryTransparent'
]);
type Saved = { value: string; priority: string };
let owner: SurfaceSession | undefined;
let current: AppearanceOverrides | undefined;
let saved: Map<string, Saved> | undefined;
let themeObserver: MutationObserver | undefined;

function validColor(value: unknown): value is string {
  return typeof value === 'string' && /^#[0-9a-fA-F]{6}$/.test(value) && CSS.supports('color', value);
}

function validate(input: AppearanceOverrides | null): AppearanceOverrides | null {
  if (input === null) return null;
  if (
    !input ||
    typeof input !== 'object' ||
    Array.isArray(input) ||
    (Object.getPrototypeOf(input) !== Object.prototype && Object.getPrototypeOf(input) !== null)
  ) {
    throw new TypeError('Appearance overrides must be a plain object');
  }
  for (const key of Object.keys(input))
    if (!allowed.has(key)) throw new TypeError('Unknown appearance field');
  for (const key of ['accent', 'accentStrong', 'cardDark', 'cardLight'] as const) {
    if (input[key] !== undefined && !validColor(input[key])) throw new TypeError('Invalid appearance color');
  }
  if (
    input.cardOpacity !== undefined &&
    (!Number.isFinite(input.cardOpacity) || input.cardOpacity < 0 || input.cardOpacity > 1)
  )
    throw new TypeError('Invalid card opacity');
  if (input.secondaryTransparent !== undefined && typeof input.secondaryTransparent !== 'boolean')
    throw new TypeError('Invalid secondary transparency');
  return Object.freeze({ ...input });
}

function restore(): void {
  if (!saved) return;
  for (const [name, old] of saved) {
    if (old.value) document.body.style.setProperty(name, old.value, old.priority);
    else document.body.style.removeProperty(name);
  }
}

function blend(color: string, opacity: number): string {
  const result = `color-mix(in srgb, ${color} ${Math.round(opacity * 10000) / 100}%, transparent)`;
  if (!CSS.supports('color', result)) throw new TypeError('Appearance blending is unavailable');
  return result;
}

function render(): void {
  restore();
  if (!current) return;
  const style = document.body.style;
  if (current.accent) {
    style.setProperty('--accent', current.accent);
    style.setProperty('--accent-soft', blend(current.accent, 0.18));
  }
  if (current.accentStrong) style.setProperty('--accent-strong', current.accentStrong);
  const cardColor = document.body.dataset.theme === 'light' ? current.cardLight : current.cardDark;
  const opacity = current.cardOpacity ?? 1;
  if (cardColor || opacity < 1)
    style.setProperty('--panel', blend(cardColor || 'var(--panel-opaque)', opacity));
  if (current.secondaryTransparent) {
    style.setProperty('--panel-soft', blend(cardColor || 'var(--panel-soft-opaque)', opacity));
    style.setProperty('--panel-hover', blend(cardColor || 'var(--panel-hover-opaque)', opacity));
  }
}

function release(): void {
  themeObserver?.disconnect();
  themeObserver = undefined;
  restore();
  saved = undefined;
  current = undefined;
  owner = undefined;
}

export function setAppearance(session: SurfaceSession, input: AppearanceOverrides | null): void {
  if (session.signal.aborted) throw new DOMException('Appearance surface is disposed', 'InvalidStateError');
  if (session.context.surfaceId !== 'application.appearance')
    throw new TypeError('Appearance requires its surface');
  const value = validate(input);
  if (owner && owner !== session)
    throw new DOMException('Appearance surface is already owned', 'InvalidStateError');
  if (value === null) {
    if (owner === session) release();
    return;
  }
  if (!owner) {
    saved = new Map(
      variables.map((name) => [
        name,
        {
          value: document.body.style.getPropertyValue(name),
          priority: document.body.style.getPropertyPriority(name)
        }
      ])
    );
    owner = session;
    themeObserver = new MutationObserver(() => render());
    themeObserver.observe(document.body, { attributes: true, attributeFilter: ['data-theme'] });
  }
  current = value;
  render();
}

export function releaseAppearance(session: SurfaceSession): void {
  if (owner === session) release();
}
