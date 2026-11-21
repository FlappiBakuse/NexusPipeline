import { NexusElement, elementAttributes } from './element-base.js';
import { controlStyles } from './control-styles.js';

const styles =
  controlStyles +
  `
button{display:grid;place-items:center;width:52px;height:40px;min-height:40px;padding:0;border:0;border-radius:999px}
button,button:hover{background:transparent}
.track{display:block;position:relative;width:100%;height:30px;overflow:hidden;border-radius:inherit;
background:var(--border-strong,#465066);transition:background .18s ease,filter .18s ease}
.thumb{position:absolute;top:5px;left:5px;width:20px;height:20px;border-radius:50%;
background:var(--content-card,#f0f3fa);box-shadow:0 1px 3px #0003;transition:transform .18s ease}
[aria-checked=true] .track{background:var(--nx-color-accent,#80a7ff)}
[aria-checked=true] .thumb{transform:translateX(22px)}
button:not(:disabled):hover .track{filter:brightness(1.12)}
button:focus-visible{outline:none}
button:focus-visible .track{outline:2px solid var(--nx-color-accent,#80a7ff);outline-offset:2px}
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
