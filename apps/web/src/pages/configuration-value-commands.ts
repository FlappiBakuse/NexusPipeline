import { draftPresentation } from '../app/draft-presentation';
import { configurationDrafts } from './configuration-drafts';
import { createIdentity } from '../control/identity';
import type { ConfigurationStore } from '../app/store-ports';
import { ControlFailure, objectValue } from '../control/client';
import { configurationOverview } from './configuration-types';
import { ConfigurationDocument, type ConfigurationValueDraft } from './configuration-document';

export class ConfigurationValueCommands {
  private readonly instance: string | undefined;
  constructor(
    private readonly store: ConfigurationStore,
    private readonly state: ConfigurationValueDraft,
    private readonly document: ConfigurationDocument,
    private readonly binding: string,
    private readonly key: string,
    private readonly changed: () => void
  ) {
    this.instance = store.state.capabilities?.instanceId;
  }

  private current(): boolean {
    return this.instance === this.store.state.capabilities?.instanceId;
  }

  get canEdit(): boolean {
    return (
      this.current() &&
      this.store.can('configuration.edit') &&
      !!this.document.resource?.isSaved &&
      !!this.document.file &&
      !!this.state.path &&
      this.document.view.parsed &&
      !this.state.busy &&
      !this.state.pending
    );
  }

  private command(): Record<string, unknown> {
    const resource = this.document.resource,
      file = this.document.file,
      state = this.state;
    if (!resource || !file || !state.path || file.sha256 !== state.sourceSha256)
      throw new Error('请先选择文件和字段。');
    const expected = this.parse(state.expectedText, state.expected),
      value = this.parse(state.valueText, state.value);
    if (!state.operationId) state.operationId = createIdentity();
    return {
      format: 'nexus.control.configuration-value',
      version: 1,
      expectedBindingRevision: state.bindingRevision,
      relativePath: state.filePath,
      syntax: state.syntax,
      patch: {
        format: 'nexus.config.patch',
        version: 1,
        operationId: state.operationId,
        resourceId: resource.resourceId,
        sourceSha256: file.sha256,
        expectedRevision: state.resourceRevision,
        edits: [{ selector: this.document.selector(), expected, value }]
      }
    };
  }

  private parse(raw: string | undefined, value: unknown): unknown {
    if (raw === undefined) return value;
    if (new TextEncoder().encode(raw).byteLength > 2 * 1024 * 1024) throw new Error('JSON 值超过 2 MiB。');
    try {
      return JSON.parse(raw);
    } catch {
      throw new Error('请先修正 JSON 输入。');
    }
  }

  async execute(action: 'preview' | 'apply'): Promise<void> {
    if (!this.canEdit) return;
    const state = this.state;
    if (action === 'apply' && !state.preview) {
      state.note = '需要先预览更改。';
      return;
    }
    state.busy = true;
    state.note = '';
    try {
      const body = this.command(),
        id = state.operationId;
      state.pending = id;
      state.pendingAction = action;
      const result = await this.store.client.write(
        `/control/v1/configuration/generic/${this.binding}/value/${action}`,
        body,
        (value) => {
          const row = objectValue(value);
          if (row.operationId !== id || typeof row.hasChanges !== 'boolean')
            throw new Error('configuration_response_invalid');
          return row;
        },
        id
      );
      if (!this.current()) return;
      state.pending = '';
      state.pendingAction = '';
      if (action === 'preview') {
        state.preview = result.hasChanges === false ? '' : id;
        state.note = result.hasChanges === false ? '无实际更改。' : '预览已完成。';
      } else this.saved();
    } catch (error) {
      if (this.current() && !(error instanceof ControlFailure && error.uncertain)) {
        state.pending = '';
        state.pendingAction = '';
      }
      this.store.report(error);
    } finally {
      state.busy = false;
    }
  }

  async inspect(): Promise<void> {
    if (!this.state.pending || this.state.busy || !this.current()) return;
    this.state.busy = true;
    try {
      if (this.state.pendingAction === 'preview') {
        await this.store.client.read(
          '/control/v1/configuration/bindings/' + this.binding + '/resources',
          configurationOverview
        );
        if (!this.current()) return;
        Object.assign(this.state, { pending: '', pendingAction: '', preview: '', operationId: '' });
        this.changed();
        return;
      }
      const result = await this.store.client.read(
        '/control/v1/operations/' + this.state.pending,
        objectValue
      );
      if (!this.current()) return;
      if (
        result.operationId === this.state.pending &&
        result.kind === 'configuration.save' &&
        result.status === 'completed'
      )
        this.saved();
      else this.state.note = '操作仍在处理中。';
    } catch (error) {
      this.state.note = '无法证明保存结果；草稿已保留。';
      this.store.report(error);
    } finally {
      this.state.busy = false;
    }
  }

  private saved(): void {
    this.document.saved();
    draftPresentation(configurationDrafts(this.store, this.binding).child('value').child('host-form'), true).saved();
    Object.assign(this.state, {
      pending: '',
      pendingAction: '',
      filePath: '',
      path: '',
      expected: null,
      value: null,
      baseline: '[null,null]',
      expectedText: undefined,
      valueText: undefined,
      preview: '',
      operationId: '',
      sourceSha256: '',
      note: '配置已保存。'
    });
    this.store.dirty.delete(this.key);
    this.changed();
  }
}
