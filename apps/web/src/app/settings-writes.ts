import { reactive } from 'vue';
import { ControlFailure } from '../control/client';
import { createIdentity } from '../control/identity';
import { copyDraft } from './draft-copy';

export type SettingsSnapshot<T> = { revision: number; values: T };
export type SettingsSubmission<T> = SettingsSnapshot<T> & { id: string };
type SettingsPort<T> = {
  current(): SettingsSnapshot<T>;
  write(value: SettingsSubmission<T>): Promise<SettingsSnapshot<T>>;
  read(): Promise<SettingsSnapshot<T>>;
  inspect(value: SettingsSubmission<T>): Promise<SettingsSnapshot<T> | undefined>;
  signal: AbortSignal;
};
const equal = (left: unknown, right: unknown): boolean => JSON.stringify(left) === JSON.stringify(right);
const object = (value: unknown): value is Record<string, unknown> =>
  value !== null && typeof value === 'object' && !Array.isArray(value);

export function rebaseSettings<T>(current: T, before: T, after: T): T {
  if (equal(before, after) || equal(current, after)) return current === undefined ? current : copyDraft(current);
  if (equal(current, before)) return after === undefined ? after : copyDraft(after);
  if (object(current) && object(before) && object(after)) {
    const result: Record<string, unknown> = { ...current };
    for (const key of new Set([...Object.keys(before), ...Object.keys(after)]))
      result[key] = rebaseSettings(current[key], before[key], after[key]);
    return result as T;
  }
  throw new Error('设置已在其他位置更改，请检查当前值。');
}

export class SettingsWrites<T extends object> {
  readonly state;
  private queued?: { before: T; after: T };
  private work?: Promise<void>;
  private confirmed: SettingsSnapshot<T>;

  constructor(private readonly port: SettingsPort<T>) {
    this.confirmed = copyDraft(port.current());
    this.state = reactive({ values: copyDraft(this.confirmed.values), busy: false,
      pending: null as SettingsSubmission<T> | null, error: '', unknown: false }) as {
        values: T; busy: boolean; pending: SettingsSubmission<T> | null; error: string; unknown: boolean;
      };
  }

  observe(snapshot: SettingsSnapshot<T>): void {
    if (!this.state.busy && !this.state.pending && !this.queued) this.state.values = copyDraft(snapshot.values);
    if (this.state.unknown && !this.work) void this.resume();
  }

  async patch(values: Partial<T>): Promise<SettingsSnapshot<T>> {
    this.port.signal.throwIfAborted();
    if (this.state.unknown) throw new Error('设置结果待确认，请稍后查看。');
    this.state.error = '';
    const before = copyDraft(this.state.values), after = { ...before, ...copyDraft(values) };
    if (!equal(before, after)) {
      this.queued = { before: this.queued?.before ?? before, after };
      this.state.values = after;
      this.state.error = '';
      this.start();
    }
    await this.settled();
    return copyDraft(this.confirmed);
  }

  private start(): void {
    if (this.work) return;
    this.state.busy = true;
    this.work = Promise.resolve().then(() => this.drain()).finally(() => {
      this.work = undefined;
      this.state.busy = false;
    });
  }

  async settled(): Promise<void> {
    await this.work;
    if (this.state.pending) throw new Error('设置结果待确认，请稍后查看。');
  }

  private async drain(): Promise<void> {
    while (this.queued && !this.port.signal.aborted) {
      const change = this.queued;
      this.queued = undefined;
      try {
        let current = this.port.current();
        let values = rebaseSettings(current.values, change.before, change.after);
        if (equal(values, current.values)) continue;
        let submitted = { id: createIdentity(), revision: current.revision, values };
        this.state.pending = submitted;
        let saved: SettingsSnapshot<T>;
        try { saved = await this.port.write(submitted); }
        catch (reason) {
          if (!(reason instanceof ControlFailure && reason.status === 409 && !reason.uncertain)) throw reason;
          current = await this.port.read();
          values = rebaseSettings(current.values, change.before, change.after);
          submitted = { id: createIdentity(), revision: current.revision, values };
          this.state.pending = submitted;
          saved = await this.port.write(submitted);
        }
        this.accept(saved);
      } catch (reason) {
        this.queued = undefined;
        if (reason instanceof ControlFailure && reason.uncertain && this.state.pending) {
          this.state.unknown = true;
          await this.inspect();
        } else {
          this.state.pending = null;
          this.state.error = reason instanceof Error ? reason.message : '设置保存失败。';
        }
        if (!this.port.signal.aborted) {
          try { this.state.values = copyDraft(this.port.current().values); }
          catch { return; }
        }
        break;
      }
    }
  }

  private accept(snapshot: SettingsSnapshot<T>): void {
    this.confirmed = copyDraft(snapshot);
    this.state.pending = null;
    this.state.unknown = false;
    this.state.error = '';
    if (!this.queued) this.state.values = copyDraft(this.port.current().values);
  }

  private async inspect(): Promise<void> {
    for (const delay of [0, 200, 800, 2000]) {
      if (delay) await new Promise<void>(resolve => {
        const finish = () => { clearTimeout(timer); this.port.signal.removeEventListener('abort', finish); resolve(); };
        const timer = setTimeout(finish, delay);
        this.port.signal.addEventListener('abort', finish, { once: true });
      });
      if (this.port.signal.aborted || !this.state.pending) return;
      try {
        const saved = await this.port.inspect(this.state.pending);
        if (saved) { this.accept(saved); return; }
      } catch { /* A failed read does not authorize another write. */ }
    }
    this.state.error = '设置结果待确认，请稍后查看。';
  }

  async resume(): Promise<void> {
    if (this.work || !this.state.pending) return;
    this.state.busy = true;
    this.work = this.inspect().finally(() => { this.work = undefined; this.state.busy = false; });
    await this.work;
  }
}
