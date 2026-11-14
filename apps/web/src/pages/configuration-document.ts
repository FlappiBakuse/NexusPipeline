import { shallowReactive, shallowRef } from 'vue';
import type { ConfigurationStore } from '../app/store-ports';
import { arrayValue, integerValue, objectValue, stringValue } from '../control/client';
import { copyDraft } from '../app/draft-copy';
import type { ConfigurationOverview } from './configuration-types';
import { valueSelector } from './value-selector';

export interface ConfigurationValueDraft extends Record<string, unknown> {
  fields?: Record<string, FieldDraft>;
  resourceId: string;
  filePath: string;
  syntax: 'json' | 'jsonWithComments' | 'yaml';
  path: string;
  expected: unknown;
  value: unknown;
  baseline: string;
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
type FieldDraft = Pick<ConfigurationValueDraft, 'expected' | 'value' | 'baseline' | 'expectedText' | 'valueText' |
  'sourceSha256' | 'bindingRevision' | 'resourceRevision' | 'preview' | 'operationId'>;
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
  private readonly restoring = shallowRef<ConfigurationValueDraft>();

  constructor(
    private readonly store: ConfigurationStore,
    readonly state: ConfigurationValueDraft,
    private readonly overview: () => ConfigurationOverview
  ) {}

  get edited(): boolean {
    const changed = (field: FieldDraft): boolean => {
      try {
        return JSON.stringify([
          field.expectedText === undefined ? field.expected : JSON.parse(field.expectedText),
          field.valueText === undefined ? field.value : JSON.parse(field.valueText)
        ]) !== field.baseline;
      } catch { return true; }
    };
    return changed(this.restoring.value || this.state) || Object.entries(this.state.fields || {})
      .some(([key, field]) => key !== this.fieldKey() && changed(field));
  }

  private clearField(): void {
    Object.assign(this.state, { expected: null, value: null, expectedText: undefined,
      valueText: undefined, baseline: '[null,null]' });
  }
  private fieldKey(path = this.state.path): string {
    return JSON.stringify([this.state.resourceId, this.state.filePath, this.state.syntax, path]);
  }
  private retainField(): void {
    if (!this.state.path || this.restoring.value) return;
    const { expected, value, baseline, expectedText, valueText, sourceSha256,
      bindingRevision, resourceRevision, preview, operationId } = this.state;
    (this.state.fields ??= {})[this.fieldKey()] = copyDraft({ expected, value, baseline, expectedText,
      valueText, sourceSha256, bindingRevision, resourceRevision, preview, operationId });
  }
  saved(): void { if (this.state.fields) delete this.state.fields[this.fieldKey()]; }

  get resource() {
    return this.overview().resources.find((item) => item.resourceId === this.state.resourceId);
  }
  get file() {
    return this.view.files.find((item) => item.relativePath === this.state.filePath);
  }

  dispose(): void {
    this.owner?.abort();
    this.owner = undefined;
    if (!this.state.pending) this.state.busy = false;
  }

  selector(): (string | number)[] {
    if (!this.view.parsed) throw new Error('源文件无法解析，字段编辑不可用。');
    return valueSelector(this.value, this.state.path);
  }

  async chooseResource(id: string): Promise<void> {
    this.dispose();
    this.retainField();
    this.clearField();
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
    this.retainField();
    this.clearField();
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
    this.retainField();
    this.state.path = path;
    const retained = this.state.fields?.[this.fieldKey(path)];
    if (retained) { this.clearField(); Object.assign(this.state, copyDraft(retained)); return; }
    this.state.expected = copyDraft(this.view.fields.find((item) => item.path === path)?.value ?? null);
    this.state.value = copyDraft(this.state.expected);
    this.state.baseline = JSON.stringify([this.state.expected, this.state.value]);
    this.state.expectedText = JSON.stringify(this.state.expected, null, 2);
    this.state.valueText = JSON.stringify(this.state.value, null, 2);
    this.state.preview = '';
    this.state.operationId = '';
  }

  async restore(): Promise<void> {
    if (!this.state.resourceId || this.state.pending || this.state.busy) return;
    const saved = copyDraft(this.state);
    this.restoring.value = saved;
    try {
      await this.chooseResource(saved.resourceId);
      if (saved.filePath && this.view.files.some((item) => item.relativePath === saved.filePath)) {
        this.state.syntax = saved.syntax;
        await this.chooseFile(saved.filePath, true);
      }
    } finally {
      Object.assign(this.state, saved, { busy: false });
      this.restoring.value = undefined;
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
