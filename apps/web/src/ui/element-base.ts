import { uiElementDefinitions, type UiElementName } from '@nexuspipeline/extension-sdk';
import { observeLocale } from './locale-observer.js';

type Definition = { properties: Record<string, string>; defaults: Record<string, unknown> };
const attributeName = (name: string): string =>
  name.replace(/[A-Z]/g, (letter) => '-' + letter.toLowerCase());

export abstract class NexusElement extends HTMLElement {
  protected readonly root = this.attachShadow({ mode: 'open', delegatesFocus: true });
  protected readonly values: Record<string, unknown> = {};
  protected connection: AbortController | undefined;
  private readonly definition: Definition;
  private updating = false;

  constructor(name: UiElementName, styles: string, markup: string) {
    super();
    this.definition = uiElementDefinitions[name];
    this.root.innerHTML = '<style>' + styles + '</style>' + markup;
    for (const [key, value] of Object.entries(this.definition.defaults)) {
      const pending = Object.getOwnPropertyDescriptor(this, key);
      this.values[key] = pending ? pending.value : structuredClone(value);
      if (pending && !this.valid(this.definition.properties[key]!, pending.value)) {
        throw new TypeError('Invalid pre-upgrade element property: ' + key);
      }
      if (pending) delete (this as unknown as Record<string, unknown>)[key];
      Object.defineProperty(this, key, {
        configurable: true,
        get: () => this.values[key],
        set: (next) => this.setProperty(key, next)
      });
    }
  }

  connectedCallback(): void {
    if (this.connection) return;
    this.connection = new AbortController();
    this.connect(this.connection.signal);
    observeLocale(this.ownerDocument, this.connection.signal, () => this.render());
    this.render();
  }

  disconnectedCallback(): void {
    this.connection?.abort();
    this.connection = undefined;
  }

  attributeChangedCallback(name: string, oldValue: string | null, next: string | null): void {
    if (oldValue === next || this.updating) return;
    const key = Object.keys(this.values).find((property) => attributeName(property) === name);
    if (key) this.readAttribute(key, next);
    else if (this.isConnected) this.render();
  }

  protected setProperty(key: string, value: unknown): void {
    const kind = this.definition.properties[key];
    if (!kind || !this.valid(kind, value)) throw new TypeError('Invalid ' + this.localName + '.' + key);
    if (Object.is(this.values[key], value)) return;
    this.values[key] = Array.isArray(value)
      ? Object.freeze(
          value.map((item) => (item && typeof item === 'object' ? Object.freeze({ ...item }) : item))
        )
      : value && typeof value === 'object' && !(value instanceof HTMLElement)
        ? Object.freeze({ ...value })
        : value;
    if (kind === 'boolean') {
      this.updating = true;
      this.toggleAttribute(attributeName(key), value === true);
      this.updating = false;
    }
    if (this.isConnected) this.render();
    if (key === 'value' && this.hasAttribute('data-observe-value'))
      this.dispatchEvent(new Event('nexus-value-updated', { bubbles: true, composed: true }));
  }

  protected change(value: unknown, source: 'pointer' | 'keyboard' | 'input' | 'assistive'): void {
    this.emit('nexus-change', { value, source });
  }

  protected emit(name: string, detail: object): void {
    this.dispatchEvent(
      new CustomEvent(name, {
        bubbles: true,
        composed: true,
        detail: Object.freeze(detail)
      })
    );
  }

  protected describe(control: HTMLElement): void {
    const ids = (this.getAttribute('aria-describedby') ?? '').split(/\s+/).filter(Boolean);
    const tree = this.getRootNode() as Document | ShadowRoot;
    control.ariaDescribedByElements = ids
      .map((id) => tree.getElementById(id))
      .filter((element): element is HTMLElement => element instanceof HTMLElement);
  }

  protected abstract connect(signal: AbortSignal): void;
  protected abstract render(): void;

  private readAttribute(key: string, value: string | null): void {
    const kind = this.definition.properties[key];
    if (key === 'value' && (this.values.type === 'password' || this.getAttribute('type') === 'password')) {
      if (value !== null) throw new TypeError('Password values require a property');
      return;
    }
    const fallback = structuredClone(this.definition.defaults[key]);
    if (value === null) this.setProperty(key, fallback);
    else if (kind === 'boolean') this.setProperty(key, true);
    else if (kind === 'date|null') this.setProperty(key, value);
    else if (kind?.includes('number')) this.setProperty(key, Number(value));
    else if (
      kind === 'string' ||
      (kind?.includes('|') &&
        !kind.includes('readonly') &&
        !kind.includes('HTMLElement') &&
        !kind.includes('date'))
    )
      this.setProperty(key, value);
    else throw new TypeError('Structured values require a property');
  }

  private valid(kind: string, value: unknown): boolean {
    if (kind === 'HTMLElement|null') return value === null || value instanceof HTMLElement;
    if (kind === 'HH:mm|string-empty')
      return typeof value === 'string' && (value === '' || /^([01]\d|2[0-3]):[0-5]\d$/.test(value));
    if (kind === 'date|null') return value === null || (typeof value === 'string' && this.dateValid(value));
    if (kind === '{from:date|null,to:date|null}')
      return Boolean(
        value &&
          typeof value === 'object' &&
          !Array.isArray(value) &&
          Object.keys(value).length === 2 &&
          'from' in value &&
          'to' in value &&
          this.valid('date|null', (value as { from: unknown }).from) &&
          this.valid('date|null', (value as { to: unknown }).to)
      );
    if (kind === 'string|readonly string[]')
      return (
        typeof value === 'string' ||
        (Array.isArray(value) &&
          value.every((item) => typeof item === 'string') &&
          new Set(value).size === value.length)
      );
    if (kind === 'readonly(file|folder)[]')
      return (
        Array.isArray(value) &&
        value.every((item) => item === 'file' || item === 'folder') &&
        new Set(value).size === value.length
      );
    if (kind === 'readonly string[]')
      return (
        Array.isArray(value) &&
        value.every((item) => typeof item === 'string' && item.length > 0) &&
        new Set(value).size === value.length
      );
    if (kind === 'readonly MenuItem[]' || kind === 'readonly Option[]' || kind === 'readonly TabItem[]')
      return (
        Array.isArray(value) &&
        value.every(
          (item) =>
            item &&
            typeof item === 'object' &&
            typeof item.id === 'string' &&
            item.id.length > 0 &&
            typeof item.label === 'string' &&
            (item.disabled === undefined || typeof item.disabled === 'boolean')
        ) &&
        new Set(value.map((item) => item.id)).size === value.length
      );
    if (kind.startsWith('readonly ')) return Array.isArray(value);
    if (kind === 'string') return typeof value === 'string';
    if (kind === 'boolean') return typeof value === 'boolean';
    return kind
      .split('|')
      .some((part) =>
        part === 'null'
          ? value === null
          : part === 'number'
            ? typeof value === 'number' && Number.isFinite(value)
            : part === 'boolean'
              ? typeof value === 'boolean'
              : part === value
      );
  }

  private dateValid(value: string): boolean {
    if (!/^\d{4}-\d{2}-\d{2}$/.test(value)) return false;
    const date = new Date(value + 'T00:00:00');
    return (
      !Number.isNaN(date.getTime()) &&
      date.getFullYear() === Number(value.slice(0, 4)) &&
      date.getMonth() + 1 === Number(value.slice(5, 7)) &&
      date.getDate() === Number(value.slice(8))
    );
  }
}

export function elementAttributes(name: UiElementName): string[] {
  const definition: Definition = uiElementDefinitions[name];
  return [
    ...Object.keys(definition.properties)
      .filter((key) => !definition.properties[key]?.startsWith('readonly '))
      .map(attributeName),
    'aria-label',
    'aria-describedby',
    'aria-invalid',
    'aria-required'
  ];
}
