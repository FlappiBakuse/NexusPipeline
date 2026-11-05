import { NexusElement, elementAttributes } from './element-base.js';
import { controlStyles } from './control-styles.js';

const styles =
  controlStyles +
  `
button{display:flex;align-items:center;justify-content:center;width:var(--nx-switch-width,52px);min-width:var(--nx-switch-width,52px);height:var(--nx-switch-height,30px);min-height:var(--nx-switch-height,30px);padding:0 2px;border:0;border-radius:999px;
background:var(--content-control,#ffffff08)}
.track{display:block;position:relative;width:var(--nx-switch-track-width,100%);height:var(--nx-switch-track-height,100%);border-radius:inherit;
background:var(--border-strong,#465066);transition:background .18s ease}
.thumb{position:absolute;top:4px;left:4px;width:20px;height:20px;border-radius:50%;
background:var(--content-card,#f0f3fa);box-shadow:0 1px 3px #0003;transition:transform .18s ease}
[aria-checked=true] .track{background:var(--nx-color-accent,#80a7ff)}
[aria-checked=true] .thumb{transform:translateX(22px)}
`;

export class NexusSwitchElement extends NexusElement {
  static readonly observedAttributes = elementAttributes('nexus-switch');
  private readonly button: HTMLButtonElement;

  constructor() {
    super(
      'nexus-switch',
      styles,
      '<button type="button" role="switch"><span class="track" aria-hidden="true"><span class="thumb"></span></span></button>'
    );
    this.button = this.root.querySelector('button')!;
  }

  protected connect(signal: AbortSignal): void {
    this.button.addEventListener(
      'click',
      (event) => {
        if (this.values.disabled) return;
        const next = !this.values.value;
        this.setProperty('value', next);
        this.change(next, event.detail === 0 ? 'keyboard' : 'pointer');
      },
      { signal }
    );
  }

  override focus(options?: FocusOptions): void {
    this.button.focus(options);
  }

  protected render(): void {
    this.button.disabled = Boolean(this.values.disabled);
    this.button.setAttribute('aria-checked', String(this.values.value));
    this.button.setAttribute('aria-label', this.getAttribute('aria-label') ?? '');
    this.describe(this.button);
  }
}
