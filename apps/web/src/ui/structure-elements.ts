import { NexusElement, elementAttributes } from './element-base.js';
import { controlStyles } from './control-styles.js';

const cardStyle = `:host{display:block;min-width:0;padding:0;border:1px solid var(--nx-color-border,#394152);
border-radius:var(--nx-radius-lg,16px);background:var(--nx-color-surface,#1e2330);color:var(--nx-color-text,#e9edf5)}
h2,h3,p{margin:0}h2{font-size:15px;line-height:1.45}h3{font-size:15px}p{margin-top:4px;font-size:12px;line-height:1.5;color:var(--nx-color-muted,#9eacc1)}
header{display:flex;align-items:start;justify-content:space-between;gap:16px;padding:16px 24px}header[hidden]{display:none}.body{padding:16px 24px 24px}.body[data-no-heading=true]{padding:24px}:host([data-surface=secondary]){background:var(--content-card-soft,#242a38)}`;

export class NexusFieldElement extends NexusElement {
  static readonly observedAttributes = elementAttributes('nexus-field');
  private readonly controlSlot: HTMLSlotElement;
  constructor() {
    super(
      'nexus-field',
      `:host{display:grid;gap:8px;min-width:0;color:var(--nx-color-text,#e9edf5);font-weight:600;--nx-input-weight:600}
      label{font-size:12px;line-height:1.55}.help{color:var(--nx-color-muted,#9eacc1);font-size:11px;line-height:1.45}.error{color:var(--nx-color-danger,#ef7070);font-size:11px;line-height:1.45}
      .error:empty,.help:empty{display:none}`,
      '<label></label><slot name="control"></slot><slot name="help"><span class="help"></span></slot><span class="error" role="alert"></span>'
    );
    this.controlSlot = this.root.querySelector('slot[name=control]')!;
  }
  protected connect(signal: AbortSignal): void {
    this.root.querySelector('label')!.addEventListener(
      'click',
      () => {
        const control = this.controlSlot.assignedElements()[0];
        if (control instanceof HTMLElement) control.focus();
      },
      { signal }
    );
    this.controlSlot.addEventListener('slotchange', () => this.bind(), { signal });
  }
  protected render(): void {
    this.root.querySelector('label')!.textContent =
      String(this.values.label) + (this.values.required ? ' *' : '');
    this.root.querySelector('.help')!.textContent = String(this.values.help);
    this.root.querySelector('.error')!.textContent = String(this.values.error);
    this.bind();
  }
  private bind(): void {
    const control = this.controlSlot.assignedElements()[0];
    if (!(control instanceof HTMLElement)) return;
    if (this.values.controlId) control.id = String(this.values.controlId);
    control.setAttribute('aria-required', String(Boolean(this.values.required)));
    control.setAttribute('aria-invalid', String(Boolean(this.values.error)));
    if (this.values.label) control.setAttribute('aria-label', String(this.values.label));
  }
}

export class SectionCard extends NexusElement {
  static readonly observedAttributes = elementAttributes('nexus-section-card');
  constructor() {
    super(
      'nexus-section-card',
      cardStyle,
      '<header><slot name="header"><div><h2></h2><p></p></div></slot><slot name="actions"></slot></header><div class="body"><slot></slot></div>'
    );
  }
  protected connect(signal: AbortSignal): void {
    this.root
      .querySelectorAll('slot[name=header],slot[name=actions]')
      .forEach((slot) => slot.addEventListener('slotchange', () => this.render(), { signal }));
  }
  protected render(): void {
    this.dataset.surface = String(this.values.surface);
    this.root.querySelector('h2')!.textContent = String(this.values.title);
    this.root.querySelector('p')!.textContent = String(this.values.description);
    const empty =
      !this.values.title &&
      !this.values.description &&
      ![...this.root.querySelectorAll<HTMLSlotElement>('slot[name=header],slot[name=actions]')].some(
        (slot) => slot.assignedNodes().length
      );
    this.root.querySelector('header')!.hidden = empty;
    (this.root.querySelector('.body') as HTMLElement).dataset.noHeading = String(empty);
  }
}

export class CollapsibleCard extends NexusElement {
  static readonly observedAttributes = elementAttributes('nexus-collapsible-card');
  private readonly panel: HTMLElement;
  constructor() {
    super(
      'nexus-collapsible-card',
      controlStyles +
        cardStyle +
        `:host{padding:0;overflow:hidden}button{width:100%;min-height:72px;display:flex;align-items:center;justify-content:space-between;text-align:left;border:0;border-radius:0;background:transparent;padding:16px 24px}
      button:hover{background:transparent}.heading{display:grid;gap:4px}.title{font-size:15px;line-height:1.45}.description{font-size:12px;color:var(--nx-color-muted);line-height:1.5}.description:empty{display:none}
      nexus-icon{color:var(--nx-color-accent)}.reveal{display:grid;grid-template-rows:0fr;opacity:0;visibility:hidden;transition:grid-template-rows .22s ease,opacity .22s ease,visibility 0s .22s}
      .reveal[data-expanded=true]{grid-template-rows:1fr;opacity:1;visibility:visible;transition-delay:0s}.clip{min-height:0;overflow:hidden}.body{margin:0;border-top:1px solid var(--nx-color-border);padding:24px}
      @media(prefers-reduced-motion:reduce){.reveal{transition:none}}`,
      '<button type="button"><span class="heading"><strong class="title"></strong><span class="description"></span></span><nexus-icon aria-hidden="true"></nexus-icon></button><slot name="actions"></slot><div class="reveal"><div class="clip"><div class="body"><slot></slot></div></div></div>'
    );
    this.panel = this.root.querySelector('.reveal')!;
  }
  protected connect(signal: AbortSignal): void {
    this.root.querySelector('button')!.addEventListener(
      'click',
      (event) => {
        const expanded = !this.values.expanded;
        this.setProperty('expanded', expanded);
        this.emit('nexus-toggle', { expanded, source: event.detail === 0 ? 'keyboard' : 'pointer' });
      },
      { signal }
    );
  }
  override focus(options?: FocusOptions): void {
    this.root.querySelector('button')!.focus(options);
  }
  protected render(): void {
    const button = this.root.querySelector('button')!;
    button.querySelector('.title')!.textContent = String(this.values.title);
    button.querySelector('.description')!.textContent = String(this.values.description);
    button.querySelector('nexus-icon')!.setAttribute('name', this.values.expanded ? 'chevronDown' : 'chevronRight');
    button.setAttribute('aria-expanded', String(this.values.expanded));
    this.panel.id = String(this.values.panelId) || 'panel-' + this.localName;
    button.setAttribute('aria-controls', this.panel.id);
    this.panel.dataset.expanded = String(this.values.expanded);
    this.panel.inert = !this.values.expanded;
  }
}

export class ActionGroup extends NexusElement {
  static readonly observedAttributes = elementAttributes('nexus-action-group');
  constructor() {
    super(
      'nexus-action-group',
      ':host{display:flex;flex-wrap:wrap;align-items:center;gap:8px;min-width:0}',
      '<slot></slot>'
    );
  }
  protected connect(_signal: AbortSignal): void {}
  protected render(): void {
    this.style.justifyContent =
      this.values.align === 'between'
        ? 'space-between'
        : this.values.align === 'end'
          ? 'flex-end'
          : 'flex-start';
  }
}

export class EntityRow extends NexusElement {
  static readonly observedAttributes = elementAttributes('nexus-entity-row');
  constructor() {
    super(
      'nexus-entity-row',
      `:host{display:grid;min-width:0;align-items:center;gap:12px;color:var(--nx-color-text,#e9edf5)}
    .title,.subtitle{min-width:0;overflow-wrap:anywhere}.title{font-weight:700}.subtitle{color:var(--nx-color-muted,#9eacc1);font-size:12px}.subtitle:empty{display:none}slot[name=actions]{display:flex;align-items:center;gap:8px}`,
      '<slot name="icon"></slot><div class="title"></div><div class="subtitle"></div><slot name="meta"></slot><slot name="actions"></slot>'
    );
  }
  protected connect(_signal: AbortSignal): void {}
  protected render(): void {
    this.dataset.itemId = String(this.values.itemId);
    this.root.querySelector('.title')!.textContent = String(this.values.title);
    this.root.querySelector('.subtitle')!.textContent = String(this.values.subtitle);
  }
}

export class ScheduleCard extends NexusElement {
  static readonly observedAttributes = elementAttributes('nexus-schedule-card');
  constructor() {
    super(
      'nexus-schedule-card',
      controlStyles +
        `:host{display:block;min-width:0}.head{display:flex;align-items:center;gap:8px;padding:13px 8px}.head>button{flex:1;display:flex;align-items:center;justify-content:space-between;min-width:0;padding:0;border:0;border-radius:0;background:transparent;text-align:left}.head>button:hover{background:transparent}.arrow{width:24px;text-align:center;color:var(--nx-color-muted)}
        .body{display:grid;grid-template-columns:minmax(0,3fr) minmax(150px,1fr);gap:16px;padding:0 0 14px 34px}.body[hidden]{display:none}slot[name=actions]{grid-column:1/-1}@media(max-width:820px){.body{grid-template-columns:1fr}}@media(max-width:480px){.body{padding-left:0}}`,
      '<div class="head"><nexus-drag-handle></nexus-drag-handle><button type="button"><span class="summary"></span><span class="arrow" aria-hidden="true"></span></button></div><div class="body"><slot name="days"></slot><slot name="time"></slot><slot name="actions"></slot></div>'
    );
  }
  protected connect(signal: AbortSignal): void {
    this.root.querySelector('button')!.addEventListener(
      'click',
      (event) => {
        const expanded = !this.values.expanded;
        this.setProperty('expanded', expanded);
        this.emit('nexus-toggle', { expanded, source: event.detail === 0 ? 'keyboard' : 'pointer' });
      },
      { signal }
    );
  }
  override focus(options?: FocusOptions): void {
    this.root.querySelector('button')!.focus(options);
  }
  protected render(): void {
    this.dataset.itemId = String(this.values.itemId);
    const button = this.root.querySelector('button')!;
    button.querySelector('.summary')!.textContent = String(this.values.summary);
    button.querySelector('.arrow')!.textContent = this.values.expanded ? '⌄' : '›';
    const handle = this.root.querySelector('nexus-drag-handle')! as HTMLElement & { itemId: string };
    handle.itemId = String(this.values.itemId);
    button.setAttribute('aria-expanded', String(this.values.expanded));
    const body = this.root.querySelector<HTMLElement>('.body')!;
    body.hidden = !this.values.expanded;
    body.inert = !this.values.expanded;
  }
}

export class PageHeader extends NexusElement {
  static readonly observedAttributes = elementAttributes('nexus-page-header');
  constructor() {
    super(
      'nexus-page-header',
      ':host{display:flex;justify-content:space-between;align-items:end;gap:20px;min-width:0;margin-bottom:24px}h1{margin:0;font-size:clamp(22px,2vw,30px);font-weight:650;letter-spacing:-.04em}p{margin:7px 0 0;font-size:12px;color:var(--nx-color-muted,#9eacc1)}',
      '<div><h1></h1><p></p></div><slot name="actions"></slot>'
    );
  }
  protected connect(_signal: AbortSignal): void {}
  protected render(): void {
    this.root.querySelector('h1')!.textContent = String(this.values.title);
    this.root.querySelector('p')!.textContent = String(this.values.description);
  }
}

export class SwitchSetting extends NexusElement {
  static readonly observedAttributes = elementAttributes('nexus-switch-setting');
  private readonly control: HTMLElement & { value: boolean; disabled: boolean };
  constructor() {
    super(
      'nexus-switch-setting',
      controlStyles +
        `:host{display:flex;align-items:center;gap:16px;min-height:64px;padding:12px 16px;border:1px solid var(--content-card-border,var(--nx-color-border));border-radius:var(--nx-radius-md);background:var(--content-card-soft,var(--nx-color-surface))}
    .copy{flex:1;min-width:0;line-height:1.5}.title{font-size:13px;font-weight:600}.description{color:var(--nx-color-muted,#9eacc1);font-size:12px;font-weight:400;margin-top:4px}
    nexus-switch{flex:none;--nx-switch-width:56px;--nx-switch-height:40px;--nx-switch-track-width:52px;--nx-switch-track-height:30px}`,
      '<div class="copy"><div class="title"></div><div class="description"></div></div><nexus-switch></nexus-switch>'
    );
    this.control = this.root.querySelector<HTMLElement & { value: boolean; disabled: boolean }>(
      'nexus-switch'
    )!;
  }
  protected connect(signal: AbortSignal): void {
    this.control.addEventListener(
      'nexus-change',
      (event) => {
        event.stopPropagation();
        const detail = (
          event as CustomEvent<{ value: boolean; source: 'pointer' | 'keyboard' | 'input' | 'assistive' }>
        ).detail;
        this.setProperty('value', detail.value);
        this.change(detail.value, detail.source);
      },
      { signal }
    );
    this.addEventListener(
      'click',
      (event) => {
        if (!event.composedPath().includes(this.control) && !this.values.disabled)
          this.control.shadowRoot?.querySelector<HTMLButtonElement>('button')?.click();
      },
      { signal }
    );
  }
  override focus(options?: FocusOptions): void {
    this.control.focus(options);
  }
  protected render(): void {
    this.root.querySelector('.title')!.textContent = String(this.values.title);
    this.root.querySelector('.description')!.textContent = String(this.values.description);
    this.control.disabled = Boolean(this.values.disabled);
    this.control.value = Boolean(this.values.value);
    this.control.setAttribute('aria-label', String(this.values.title));
  }
}

export class SwitchList extends NexusElement {
  static readonly observedAttributes = elementAttributes('nexus-switch-list');
  constructor() {
    super(
      'nexus-switch-list',
      ':host{display:grid;gap:0;border:1px solid var(--content-card-border,var(--nx-color-border));border-radius:var(--nx-radius-md);overflow:hidden}h3{margin:0;padding:12px 16px;font-size:14px;color:var(--nx-color-muted,#9eacc1)}h3:empty{display:none}::slotted(nexus-switch-setting){border:0;border-radius:0;background:transparent}',
      '<h3></h3><slot></slot>'
    );
  }
  protected connect(_signal: AbortSignal): void {}
  protected render(): void {
    this.root.querySelector('h3')!.textContent = String(this.values.label);
  }
}
