import { reactive, type Directive } from 'vue';
import type { EditorDraftScope } from '@nexuspipeline/extension-sdk';
import './draft-presentation.css';

type Field = HTMLElement & { value: unknown; checked?: boolean; type?: string };
type Baseline = { value: unknown; changed: boolean; sensitive: boolean };
const fields = 'input:not([type=hidden]):not([type=button]):not([type=submit]),textarea,select,nexus-text-input,nexus-text-area,nexus-number-input,nexus-switch,nexus-switch-setting,nexus-select,nexus-path-picker,nexus-file-picker,nexus-color-picker,nexus-range,nexus-date-range-picker,nexus-time-picker';
const same = (a: unknown, b: unknown): boolean => {
  if (a === b) return true;
  if (!a || !b || typeof a !== 'object' || typeof b !== 'object' || Array.isArray(a) !== Array.isArray(b)) return false;
  const left = a as Record<string, unknown>, right = b as Record<string, unknown>, keys = Object.keys(left);
  return keys.length === Object.keys(right).length && keys.every(key => Object.hasOwn(right, key) && same(left[key], right[key]));
};

export class DraftPresentation {
  readonly state = reactive({ restored: false, changed: false });
  private readonly baseline = new Map<string, Baseline>();
  private readonly nodes = new Map<Field, string>();
  private root?: HTMLElement;
  private observer?: MutationObserver;
  private controller?: AbortController;
  private retained = false;
  edited?: () => boolean;
  get modified(): boolean { return this.edited?.() ?? this.state.changed; }

  constructor(public existing: boolean) {}

  attach(root: HTMLElement): void {
    if (this.root === root) return;
    this.detach();
    this.state.restored = this.retained;
    this.root = root;
    root.classList.add('draft-surface');
    this.controller = new AbortController();
    const signal = this.controller.signal;
    const edit = (event: Event) => {
      const input = event.composedPath().find((node): node is Field => node instanceof HTMLElement && this.nodes.has(node as Field));
      if (!input) return;
      const saved = this.baseline.get(this.nodes.get(input)!)!;
      if (event.type === 'nexus-value-updated' && saved.sensitive) return;
      const detail = (event as CustomEvent<{ value?: unknown }>).detail;
      const origin = event.composedPath()[0];
      let value = event.type === 'nexus-change' && detail && 'value' in detail ? detail.value : this.value(input);
      if (event.type === 'input' && origin instanceof HTMLInputElement && input.localName === 'nexus-number-input')
        value = origin.value.trim() && Number.isFinite(Number(origin.value)) ? Number(origin.value) : origin.value;
      value = this.normalized(input, value);
      saved.changed = saved.sensitive ? value !== '' && value !== undefined : !same(saved.value, value);
      this.mark(input, saved);
      this.state.changed = [...this.baseline.values()].some(item => item.changed);
    };
    for (const name of ['input', 'change', 'nexus-change', 'nexus-value-updated']) root.addEventListener(name, edit, { signal });
    this.scan();
    this.observer = new MutationObserver(records => {
      if (records.some(record => [...record.addedNodes].some(node => node instanceof Element &&
        (node.matches(fields) || node.querySelector(fields))) || record.removedNodes.length)) this.scan();
    });
    this.observer.observe(root, { childList: true, subtree: true });
  }

  private value(field: Field): unknown {
    if (field instanceof HTMLInputElement && ['checkbox', 'radio'].includes(field.type)) return field.checked;
    return this.normalized(field, field.value);
  }

  private normalized(field: Field, value: unknown): unknown {
    if (typeof value === 'string' && field.closest('[data-draft-json]')) {
      try { return JSON.parse(value); } catch { return value; }
    }
    return value;
  }

  private scan(): void {
    if (!this.root) return;
    this.nodes.clear();
    const counts = new Map<string, number>();
    for (const element of this.root.querySelectorAll<Field>(fields)) {
      const contribution = element.closest('[data-extension-id]');
      if (contribution && this.root.contains(contribution)) continue;
      const label = element.closest('label');
      const caption = element.getAttribute('data-draft-field') || element.closest('[data-draft-field]')?.getAttribute('data-draft-field') || element.getAttribute('aria-label') || element.getAttribute('name') ||
        (label ? [...label.childNodes].filter(node => node.nodeType === Node.TEXT_NODE).map(node => node.textContent).join(' ').trim() ||
          label.querySelector('span,strong')?.textContent : '') || element.id;
      if (!caption) continue;
      const item = (element.closest('[data-draft-item],[data-item-id]')?.getAttribute('data-draft-item') ||
        element.closest('[data-item-id]')?.getAttribute('data-item-id') || '') + '/' +
        (element.closest('[data-secret-field]')?.getAttribute('data-secret-field') || '');
      const base = JSON.stringify([item, caption.replace(/\s+/g, ' ').trim(), element.localName, element.type]);
      const ordinal = counts.get(base) || 0;
      counts.set(base, ordinal + 1);
      const key = base + '/' + ordinal;
      this.nodes.set(element, key);
      element.setAttribute('data-observe-value', '');
      const sensitive = element.type === 'password' || element.hasAttribute('data-sensitive') || !!element.closest('[data-secret-field]');
      if (!this.baseline.has(key)) this.baseline.set(key, { sensitive, changed: false,
        value: sensitive ? undefined : this.value(element) && typeof this.value(element) === 'object' ?
          JSON.parse(JSON.stringify(this.value(element))) : this.value(element) });
      const saved = this.baseline.get(key)!;
      if (!saved.sensitive) saved.changed = !same(saved.value, this.value(element));
      this.mark(element, saved);
    }
    this.state.changed = [...this.baseline.values()].some(item => item.changed);
  }

  private mark(element: Field, saved: Baseline): void {
    if (this.existing && this.state.restored && saved.changed) element.setAttribute('data-unsaved', 'true');
    else element.removeAttribute('data-unsaved');
  }

  saved(): void {
    this.existing = true;
    this.baseline.clear();
    this.state.restored = false;
    this.state.changed = false;
    this.retained = false;
    this.scan();
  }
  detach(): void {
    if (this.root) this.retained = this.modified;
    this.controller?.abort(); this.observer?.disconnect();
    for (const element of this.nodes.keys()) element.removeAttribute('data-observe-value');
    for (const [key, value] of this.baseline) if (!value.changed) this.baseline.delete(key);
    this.nodes.clear(); this.root = undefined;
  }
  clear(): void { this.edited = undefined; this.detach(); this.baseline.clear(); this.retained = false; this.state.changed = false; }
}

export function draftPresentation(scope: EditorDraftScope, existing: boolean, edited?: () => boolean): DraftPresentation {
  const presentation = scope.protect('presentation', () => new DraftPresentation(existing), value => value.clear());
  if (edited) presentation.edited = edited;
  return presentation;
}

export const draftDirective: Directive<HTMLElement, DraftPresentation> = {
  mounted: (element, binding) => binding.value.attach(element),
  updated: (element, binding) => { if (binding.oldValue !== binding.value) binding.oldValue?.detach(); binding.value.attach(element); },
  beforeUnmount: (_element, binding) => binding.value.detach()
};
