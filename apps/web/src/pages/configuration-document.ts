import { shallowReactive } from 'vue';
import type { ConfigurationStore } from '../app/store-ports';
import { arrayValue, integerValue, objectValue, stringValue } from '../control/client';
import { copyDraft } from '../app/draft-copy';
import type { ConfigurationOverview } from './configuration-types';
import { valueSelector } from './value-selector';

export interface ConfigurationValueDraft extends Record<string, unknown> {
  resourceId: string;
  filePath: string;
  syntax: 'json' | 'jsonWithComments' | 'yaml';
  path: string;
  expected: unknown;
  value: unknown;
  expectedText?: string;
  valueText?: string;
  preview: string;
  operationId: string;
  pending: string;
  pendingAction: 'preview' | 'apply' | '';
  sourceSha256: string;
  bindingRevision: number;
  resourceRevision: number;
  busy: boolean;
  note: string;
}
interface ConfigurationFile {
  position: number;
  relativePath: string;
  sizeBytes: number;
  sha256: string;
}
interface ConfigurationField {
  path: string;
  value: unknown;
}

export class ConfigurationDocument {
  readonly view = shallowReactive({
    files: [] as ConfigurationFile[],
    fields: [] as ConfigurationField[],
    parsed: false
  });
  private owner?: AbortController;
  private value: unknown;

  constructor(
    private readonly store: ConfigurationStore,
    readonly state: ConfigurationValueDraft,
    private readonly overview: () => ConfigurationOverview
  ) {}

  get resource() {
    return this.overview().resources.find((item) => item.resourceId === this.state.resourceId);
  }
  get file() {
    return this.view.files.find((item) => item.relativePath === this.state.filePath);
  }

  dispose(): void {
    this.owner?.abort();
  }

  selector(): (string | number)[] {
    if (!this.view.parsed) throw new Error('源文件无法解析，字段编辑不可用。');
    return valueSelector(this.value, this.state.path);
  }

  async chooseResource(id: string): Promise<void> {
    this.dispose();
    const owner = new AbortController();
    this.owner = owner;
    Object.assign(this.state, {
      resourceId: id,
      filePath: '',
      path: '',
      preview: '',
      sourceSha256: '',
      note: ''
    });
    this.view.files = [];
    this.view.fields = [];
    this.view.parsed = false;
    const resource = this.resource;
    if (!resource?.isSaved) {
      this.state.note = '配置资源尚未保存，无法编辑字段。';
      return;
    }
    this.state.busy = true;
    try {
      let after = -1;
      do {
        const route = `/control/v1/configuration/generic/${this.overview().bindingId}/${encodeURIComponent(id)}/files?revision=${resource.revision}&afterPosition=${after}&limit=100`;
        const page = await this.store.client.read(route, objectValue, owner.signal);
        owner.signal.throwIfAborted();
        const files = arrayValue(page.items, (value) => {
          const file = objectValue(value);
          return {
            position: integerValue(file.position),
            relativePath: stringValue(file.relativePath),
            sizeBytes: integerValue(file.sizeBytes),
            sha256: stringValue(file.sha256)
          };
        });
        const next = page.nextPosition == null ? -1 : integerValue(page.nextPosition);
        if (next !== -1 && next <= after) throw new Error('configuration_file_cursor_invalid');
        this.view.files = [...this.view.files, ...files].slice(0, 4096);
        after = next;
      } while (after >= 0 && this.view.files.length < 4096);
      if (this.view.files.length === 1) await this.chooseFile(this.view.files[0]!.relativePath);
    } catch (error) {
      if (!owner.signal.aborted) this.store.report(error);
    } finally {
      if (this.owner === owner) this.state.busy = false;
    }
  }

  async chooseFile(path: string, keepSyntax = false): Promise<void> {
    this.dispose();
    const owner = new AbortController();
    this.owner = owner;
    Object.assign(this.state, { filePath: path, path: '', preview: '', note: '' });
    this.view.fields = [];
    this.view.parsed = false;
    this.value = undefined;
    const file = this.file,
      resource = this.resource;
    if (!file || !resource) return;
    this.state.sourceSha256 = file.sha256;
    this.state.bindingRevision = this.overview().bindingRevision;
    this.state.resourceRevision = resource.revision;
    if (!keepSyntax)
      this.state.syntax = /\.ya?ml$/i.test(path || resource.label)
        ? 'yaml'
        : /\.jsonc$/i.test(path || resource.label)
          ? 'jsonWithComments'
          : 'json';
    const query = new URLSearchParams({
      revision: String(resource.revision),
      sourceSha256: file.sha256,
      syntax: this.state.syntax
    });
    if (path) query.set('relativePath', path);
    this.state.busy = true;
    try {
      const route =
        '/control/v1/configuration/generic/' +
        this.overview().bindingId +
        '/' +
        encodeURIComponent(resource.resourceId) +
        '/document?' +
        query;
      const result = await this.store.client.read(route, objectValue, owner.signal);
      owner.signal.throwIfAborted();
      if (
        result.revision !== resource.revision ||
        result.sourceSha256 !== file.sha256 ||
        !Object.hasOwn(result, 'value')
      )
        throw new Error('configuration_document_response_invalid');
      this.value = result.value;
      this.view.parsed = true;
      this.view.fields = this.collect(result.value);
    } catch (error) {
      if (!owner.signal.aborted) {
        this.state.note = '源文件无法解析，字段编辑不可用。';
        this.store.report(error);
      }
    } finally {
      if (this.owner === owner) this.state.busy = false;
    }
  }

  chooseField(path: string): void {
    this.state.path = path;
    this.state.expected = copyDraft(this.view.fields.find((item) => item.path === path)?.value ?? null);
    this.state.value = copyDraft(this.state.expected);
    this.state.expectedText = JSON.stringify(this.state.expected, null, 2);
    this.state.valueText = JSON.stringify(this.state.value, null, 2);
    this.state.preview = '';
    this.state.operationId = '';
  }

  async restore(): Promise<void> {
    if (!this.state.resourceId || this.state.pending || this.state.busy) return;
    const saved = copyDraft(this.state);
    try {
      await this.chooseResource(saved.resourceId);
      if (saved.filePath && this.view.files.some((item) => item.relativePath === saved.filePath)) {
        this.state.syntax = saved.syntax;
        await this.chooseFile(saved.filePath, true);
      }
    } finally {
      Object.assign(this.state, saved, { busy: false });
    }
  }

  private collect(value: unknown): ConfigurationField[] {
    const result: ConfigurationField[] = [];
    const pending = [{ value, path: '', depth: 0 }];
    while (pending.length && result.length < 500) {
      const node = pending.pop()!;
      if (node.depth > 12) continue;
      const entries = Array.isArray(node.value)
        ? node.value.map((item, index) => [String(index), item] as const)
        : node.value && typeof node.value === 'object'
          ? Object.entries(node.value)
          : [];
      for (let index = entries.length - 1; index >= 0; index--) {
        const [name, value] = entries[index]!;
        pending.push({
          value,
          path: node.path + '/' + name.replace(/~/g, '~0').replace(/\//g, '~1'),
          depth: node.depth + 1
        });
      }
      if (node.path) result.push({ path: node.path, value: node.value });
    }
    return result;
  }
}
