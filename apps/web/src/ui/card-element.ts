import { NexusElement, elementAttributes } from './element-base.js';

export class NexusCardElement extends NexusElement {
  static readonly observedAttributes = elementAttributes('nexus-card');

  constructor() {
    super(
      'nexus-card',
      `
      :host{display:block;min-width:0;border:1px solid var(--nx-color-border,#394152);
      border-radius:var(--nx-radius-lg,14px);padding:var(--nx-space-5,24px);
      color:var(--nx-color-text,#e9edf5);background:var(--content-card,#1e2330)}
      :host([data-surface=secondary]){background:var(--content-card-secondary,#242a38)}
      :host([data-surface=nested]){background:var(--content-control,#ffffff08)}
    `,
      '<slot></slot>'
    );
  }

  protected connect(_signal: AbortSignal): void {}

  protected render(): void {
    this.dataset.surface = String(this.values.surface);
    if (this.values.accessibleName) this.setAttribute('aria-label', String(this.values.accessibleName));
    else this.removeAttribute('aria-label');
  }
}
