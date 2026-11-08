import { createIdentity } from '../control/identity';
import { observeLocale } from './locale-observer.js';
type Axis = 'x' | 'y';
type Geometry = { range: number; travel: number; offset: number };
type ScrollDrag = { axis: Axis; pointer: number; start: number; offset: number; thumb: HTMLElement };
export const scrollRailStyles = `.rail {
  position: absolute;
  z-index: 2;
  pointer-events: none;
  opacity: 0;
  transition: opacity var(--nx-motion-duration-fast,160ms) var(--nx-motion-ease,ease);
}
.rail[hidden] { display: none; }
.rail[data-axis=y] { top: 4px; right: 2px; bottom: 4px; width: 8px; }
.rail[data-axis=x] { left: 4px; right: 4px; bottom: 2px; height: 8px; }
:host([data-scroll-active]) .rail { opacity: 1; }
.thumb {
  position: absolute;
  border-radius: 999px;
  background: color-mix(in srgb,var(--nx-color-text,#7d8798) 48%,transparent);
  pointer-events: none;
  cursor: grab;
  touch-action: none;
}
.thumb:hover, .thumb:focus-visible {
  background: color-mix(in srgb,var(--nx-color-text,#7d8798) 68%,transparent);
  outline: none;
}
.thumb:active { cursor: grabbing; }
:host([data-scroll-active]) .thumb { pointer-events: auto; }
[data-axis=y] > .thumb { top: 0; right: 0; width: 100%; }
[data-axis=x] > .thumb { bottom: 0; left: 0; height: 100%; }
@media(prefers-reduced-motion:reduce) { .rail { transition: none; } }
`;
export const scrollRailMarkup = `
  <div class="rail" data-axis="x" hidden><div class="thumb" role="scrollbar" tabindex="0" aria-orientation="horizontal"></div></div>
  <div class="rail" data-axis="y" hidden><div class="thumb" role="scrollbar" tabindex="0" aria-orientation="vertical"></div></div>`;

export class ScrollRails {
  private readonly geometry = new Map<Axis, Geometry>();
  private drag: ScrollDrag | undefined;
  private frame = 0;
  private hideTimer = 0;

  constructor(
    private readonly host: HTMLElement,
    private readonly root: ShadowRoot,
    private readonly viewport: HTMLElement,
    private readonly inside: HTMLElement,
    private readonly direction: () => unknown
  ) {
    if (!viewport.id) viewport.id = 'scroll-' + createIdentity();
  }
  connect(signal: AbortSignal): void {
    observeLocale(this.host.ownerDocument, signal, () => this.schedule());
    const observer = new ResizeObserver(() => this.schedule());
    observer.observe(this.viewport);
    observer.observe(this.inside);
    this.viewport.addEventListener(
      'scroll',
      () => {
        this.reveal();
        this.schedule();
      },
      { signal, passive: true }
    );
    for (const axis of ['x', 'y'] as const) {
      const thumb = this.root.querySelector<HTMLElement>(`[data-axis=${axis}] > .thumb`)!;
      thumb.addEventListener('pointerdown', (event) => this.beginDrag(event, axis, thumb), { signal });
      thumb.addEventListener('pointermove', (event) => this.moveDrag(event), { signal });
      thumb.addEventListener('pointerup', () => this.endDrag(), { signal });
      thumb.addEventListener('pointercancel', () => this.endDrag(), { signal });
      thumb.addEventListener(
        'lostpointercapture',
        () => {
          this.drag = undefined;
        },
        { signal }
      );
      thumb.addEventListener('keydown', (event) => this.key(event, axis), { signal });
    }
    this.viewport.addEventListener('input', () => this.schedule(), { signal });
    signal.addEventListener(
      'abort',
      () => {
        observer.disconnect();
        cancelAnimationFrame(this.frame);
        clearTimeout(this.hideTimer);
        this.frame = 0;
        this.endDrag();
        this.host.removeAttribute('data-scroll-active');
      },
      { once: true }
    );
    this.schedule();
  }

  schedule(): void {
    if (!this.host.isConnected || this.frame) return;
    this.frame = requestAnimationFrame(() => {
      this.frame = 0;
      this.measure();
    });
  }

  private measure(): void {
    for (const axis of ['x', 'y'] as const) {
      const rail = this.root.querySelector<HTMLElement>(`.rail[data-axis=${axis}]`)!;
      const thumb = rail.firstElementChild as HTMLElement;
      const visible = axis === 'y' ? this.viewport.clientHeight : this.viewport.clientWidth;
      const extent = axis === 'y' ? this.viewport.scrollHeight : this.viewport.scrollWidth;
      const offset = axis === 'y' ? this.viewport.scrollTop : this.viewport.scrollLeft;
      const permitted =
        this.direction() === 'both' || this.direction() === (axis === 'y' ? 'vertical' : 'horizontal');
      rail.hidden = !permitted || visible <= 0 || extent <= visible + 1;
      const length = axis === 'y' ? rail.clientHeight : rail.clientWidth;
      const size = Math.min(length, Math.max(28, (length * visible) / Math.max(1, extent)));
      const range = Math.max(0, extent - visible),
        travel = Math.max(0, length - size);
      this.geometry.set(axis, { range, travel, offset });
      thumb.style[axis === 'y' ? 'height' : 'width'] = size + 'px';
      thumb.style.transform = `translate${axis.toUpperCase()}(${range ? (offset / range) * travel : 0}px)`;
      thumb.setAttribute('aria-controls', this.viewport.id);
      thumb.setAttribute('aria-valuemin', '0');
      thumb.setAttribute('aria-valuemax', '100');
      thumb.setAttribute('aria-valuenow', String(range ? Math.round((offset / range) * 100) : 0));
      const chinese = this.host.ownerDocument.documentElement.lang.startsWith('zh');
      thumb.setAttribute(
        'aria-label',
        chinese
          ? axis === 'y'
            ? '垂直滚动'
            : '水平滚动'
          : axis === 'y'
            ? 'Vertical scroll'
            : 'Horizontal scroll'
      );
    }
  }

  private reveal(): void {
    this.host.setAttribute('data-scroll-active', '');
    clearTimeout(this.hideTimer);
    this.hideTimer = window.setTimeout(() => this.host.removeAttribute('data-scroll-active'), 2000);
  }

  private beginDrag(event: PointerEvent, axis: Axis, thumb: HTMLElement): void {
    if (event.button !== 0) return;
    this.measure();
    this.drag = {
      axis,
      pointer: event.pointerId,
      thumb,
      start: axis === 'y' ? event.clientY : event.clientX,
      offset: this.geometry.get(axis)!.offset
    };
    event.preventDefault();
    thumb.focus();
    thumb.setPointerCapture(event.pointerId);
    this.reveal();
  }

  private moveDrag(event: PointerEvent): void {
    const drag = this.drag;
    if (!drag || drag.pointer !== event.pointerId) return;
    const metric = this.geometry.get(drag.axis)!;
    if (!metric.travel) return;
    const delta = (drag.axis === 'y' ? event.clientY : event.clientX) - drag.start;
    this.setOffset(drag.axis, drag.offset + (delta * metric.range) / metric.travel);
  }

  private endDrag(): void {
    const drag = this.drag;
    this.drag = undefined;
    if (drag?.thumb.hasPointerCapture(drag.pointer)) drag.thumb.releasePointerCapture(drag.pointer);
  }

  private setOffset(axis: Axis, offset: number): void {
    if (axis === 'y') this.viewport.scrollTop = offset;
    else this.viewport.scrollLeft = offset;
    this.reveal();
    this.schedule();
  }

  private key(event: KeyboardEvent, axis: Axis): void {
    this.measure();
    const metric = this.geometry.get(axis)!;
    const page = axis === 'y' ? this.viewport.clientHeight : this.viewport.clientWidth;
    const backwards = axis === 'y' ? 'ArrowUp' : 'ArrowLeft',
      forwards = axis === 'y' ? 'ArrowDown' : 'ArrowRight';
    const next =
      event.key === backwards
        ? metric.offset - 40
        : event.key === forwards
          ? metric.offset + 40
          : event.key === 'PageUp'
            ? metric.offset - page
            : event.key === 'PageDown'
              ? metric.offset + page
              : event.key === 'Home'
                ? 0
                : event.key === 'End'
                  ? metric.range
                  : undefined;
    if (next === undefined) return;
    event.preventDefault();
    this.setOffset(axis, next);
  }
}
