import { createIdentity } from '../control/identity';
import { reactive } from 'vue';
import type { AppStore } from '../app/store';
import { copyDraft } from '../app/draft-copy';
import { ControlFailure, integerValue, objectValue, stringValue } from '../control/client';
import {
  extensionList,
  extensionState,
  type ExtensionState,
  type ExtensionCatalog,
  type CatalogEntry
} from '../control/extensions';
import { completedSettings, savedSettings } from '../control/settings-command';
import type {
  ControlExtensionApply,
  ControlExtensionReconcile,
  ControlHostSettings
} from '../control/wire.generated';

interface PluginCommand {
  kind: 'apply' | 'setting' | 'reconcile';
  extensionId: string;
  apply: ControlExtensionApply | null;
  setting: ControlHostSettings | null;
  reconcile: ControlExtensionReconcile | null;
  journalId: string;
}
interface PluginSession {
  pending: PluginCommand | null;
  busy: boolean;
  error: string;
  notice: string;
  retryable: boolean;
}

export class PluginOwner {
  readonly state: PluginSession;
  private readonly instance: string;
  private readonly key: string;
  constructor(private readonly store: AppStore) {
    this.instance = store.state.capabilities!.instanceId;
    this.key = 'extensions/' + this.instance;
    if (!store.drafts.has(this.key))
      store.drafts.set(
        this.key,
        reactive({ pending: null, busy: false, error: '', notice: '', retryable: false })
      );
    this.state = store.drafts.get(this.key) as unknown as PluginSession;
  }
  private current(): boolean {
    return this.store.state.capabilities?.instanceId === this.instance;
  }
  private mark(): void {
    if (this.state.pending) this.store.dirty.add(this.key);
    else this.store.dirty.delete(this.key);
  }
  private finish(notice: string): void {
    const id = this.state.pending?.apply?.operationId || this.state.pending?.setting?.operationId;
    if (this.current()) {
      if (this.store.state.uncertainOperation === id) this.store.state.uncertainOperation = '';
      if (this.store.state.error === this.state.error) this.store.state.error = '';
    }
    this.state.pending = null;
    this.state.error = '';
    this.state.notice = notice;
    this.state.retryable = false;
    this.mark();
  }
  private failed(reason: unknown): void {
    this.state.error = reason instanceof Error ? reason.message : '插件操作失败。';
    if (this.current()) this.store.report(reason);
  }
  async apply(
    item: CatalogEntry | ExtensionState,
    action: 'install' | 'upgrade' | 'remove',
    installed: ExtensionState | undefined,
    catalog: ExtensionCatalog | null
  ): Promise<void> {
    const saved = this.store.state.settings?.saved;
    if (
      !this.current() ||
      !saved ||
      this.state.busy ||
      this.state.pending ||
      !this.store.can('extensions.write')
    )
      return;
    const body: ControlExtensionApply = {
      format: 'nexus.control.extension-apply',
      version: 1,
      operationId: createIdentity(),
      command: {
        extensionId: item.extensionId,
        action,
        expectedInstalledRevision: installed?.installedRevision || 0,
        expectedSettingsRevision: saved.revision,
        catalog: action === 'remove' ? null : copyDraft(catalog?.identity),
        candidate: action === 'remove' ? null : copyDraft(item)
      }
    };
    this.state.pending = {
      kind: 'apply',
      extensionId: item.extensionId,
      apply: body,
      setting: null,
      reconcile: null,
      journalId: ''
    };
    this.state.busy = true;
    this.state.error = '';
    this.state.notice = '';
    this.mark();
    try {
      const accepted = await this.store.client.write(
        '/control/v1/extensions/apply',
        body,
        (value) => {
          const row = objectValue(value);
          if (row.operationId !== body.operationId || row.status !== 'accepted')
            throw new TypeError('extension_acceptance_unbound');
          return stringValue(row.journalId);
        },
        body.operationId
      );
      this.state.pending!.journalId = accepted;
      await this.inspectApply(this.state.pending!);
    } catch (reason) {
      if (!(reason instanceof ControlFailure && reason.uncertain) && !this.state.pending?.journalId)
        this.state.pending = null;
      this.failed(reason);
    } finally {
      this.state.busy = false;
      this.mark();
    }
  }
  private async inspectApply(pending: PluginCommand): Promise<void> {
    const body = pending.apply!;
    const result = await this.store.client.read(
      '/control/v1/extensions/operations/' + body.operationId,
      objectValue
    );
    const operation = objectValue(result.operation);
    if (
      operation.operationId !== body.operationId ||
      operation.kind !== 'extension.apply' ||
      result.extensionId !== body.command.extensionId ||
      result.action !== body.command.action ||
      (pending.journalId && operation.initialJournalId !== pending.journalId)
    )
      throw new TypeError('extension_operation_unbound');
    if (operation.status !== 'completed') {
      this.state.error = '插件操作尚未完成，请稍后读取结果。';
      return;
    }
    if (
      result.phase !== 'committed' ||
      !['installed', 'removed', 'originalRestored', 'notExecuted'].includes(String(result.outcome))
    )
      throw new TypeError('extension_completion_invalid');
    if (result.outcome === 'installed' || result.outcome === 'removed') {
      if (
        integerValue(result.resultInstalledRevision) !== body.command.expectedInstalledRevision + 1 ||
        result.resultIsInstalled !== (result.outcome === 'installed')
      )
        throw new TypeError('extension_revision_unbound');
    }
    if (this.state.pending !== pending) return;
    this.finish(
      result.outcome === 'originalRestored'
        ? '插件操作未完成，原版本已恢复。'
        : result.outcome === 'notExecuted'
          ? '插件操作未执行。'
          : '插件操作已完成。'
    );
    if (this.current()) await this.store.refresh();
  }
  async enable(item: ExtensionState): Promise<void> {
    const saved = this.store.state.settings?.saved;
    if (
      !this.current() ||
      !saved ||
      this.state.busy ||
      this.state.pending ||
      !this.store.can('extensions.write') ||
      !this.store.can('settings.write')
    )
      return;
    const values = copyDraft(saved.values);
    const body: ControlHostSettings = {
      format: 'nexus.control.host-settings',
      version: 1,
      operationId: createIdentity(),
      expectedRevision: saved.revision,
      values: {
        ...values,
        extensions: [
          ...values.extensions.filter((value) => value.extensionId !== item.extensionId),
          { extensionId: item.extensionId, enabled: !item.desiredEnabled }
        ].sort((left, right) => left.extensionId < right.extensionId ? -1 : left.extensionId > right.extensionId ? 1 : 0)
      }
    };
    this.state.pending = {
      kind: 'setting',
      extensionId: item.extensionId,
      apply: null,
      setting: body,
      reconcile: {
        extensionId: item.extensionId,
        expectedInstalledRevision: item.installedRevision,
        expectedSettingsRevision: saved.revision + 1
      },
      journalId: ''
    };
    const pending = this.state.pending;
    this.state.busy = true;
    this.state.error = '';
    this.state.notice = '';
    this.mark();
    try {
      await this.store.client.write(
        '/control/v1/settings',
        body,
        (value) => savedSettings(value, body),
        body.operationId
      );
      pending.kind = 'reconcile';
      if (this.current()) {
        await this.store.refresh();
        await this.reconcile(pending);
      }
    } catch (reason) {
      if (pending.kind === 'setting' && !(reason instanceof ControlFailure && reason.uncertain))
        this.state.pending = null;
      this.failed(reason);
    } finally {
      this.state.busy = false;
      this.mark();
    }
  }
  private async reconcile(pending: PluginCommand): Promise<void> {
    this.state.retryable = false;
    await this.store.client.write('/control/v1/extensions/reconcile', pending.reconcile, (value) => {
      const result = extensionState(value);
      if (
        result.extensionId !== pending.extensionId ||
        result.installedRevision !== pending.reconcile!.expectedInstalledRevision
      )
        throw new TypeError('extension_reconcile_unbound');
      return result;
    });
    await this.inspectReconcile(pending);
  }
  private async inspectReconcile(pending: PluginCommand): Promise<void> {
    await this.store.refresh();
    const saved = this.store.state.settings!.saved,
      command = pending.reconcile!;
    const result = await this.store.client.read('/control/v1/extensions', extensionList);
    const current = result.items.find((item) => item.extensionId === pending.extensionId);
    const enabled = pending.setting!.values.extensions.find(
      (item) => item.extensionId === pending.extensionId
    )!.enabled;
    if (
      !current ||
      current.installedRevision !== command.expectedInstalledRevision ||
      saved.revision < command.expectedSettingsRevision
    )
      throw new TypeError('extension_target_changed');
    if (current.desiredEnabled === enabled && (enabled ? !!current.activationId : !current.activationId)) {
      this.finish('插件状态已更新。');
      return;
    }
    // Reconciliation has no durable operation ID; only an explicit retry may repeat an unresolved activation.
    this.state.retryable =
      saved.revision === command.expectedSettingsRevision && current.desiredEnabled === enabled;
    this.state.error = current.unavailableReason || '偏好已保存，运行状态尚未确认。';
  }
  async inspect(): Promise<void> {
    const pending = this.state.pending;
    if (!this.current() || !pending || this.state.busy || !this.store.can('operations.read')) return;
    this.state.busy = true;
    try {
      if (pending.kind === 'apply') await this.inspectApply(pending);
      else {
        if (pending.kind === 'setting') {
          const operation = await this.store.client.read(
            '/control/v1/operations/' + pending.setting!.operationId,
            objectValue
          );
          if (!completedSettings(operation, pending.setting!))
            throw new TypeError('settings_operation_unbound');
          pending.kind = 'reconcile';
        }
        await this.inspectReconcile(pending);
      }
    } catch (reason) {
      this.failed(reason);
    } finally {
      this.state.busy = false;
      this.mark();
    }
  }
  async retry(): Promise<void> {
    const pending = this.state.pending;
    if (
      !this.current() ||
      !pending ||
      pending.kind !== 'reconcile' ||
      !this.state.retryable ||
      this.state.busy ||
      !this.store.can('extensions.write')
    )
      return;
    this.state.busy = true;
    try {
      await this.inspectReconcile(pending);
      if (this.state.pending === pending && this.state.retryable) await this.reconcile(pending);
    } catch (reason) {
      this.failed(reason);
    } finally {
      this.state.busy = false;
      this.mark();
    }
  }
}
