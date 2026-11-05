import { arrayValue, booleanValue, integerValue, objectValue, stringValue } from '../control/client';

export interface ConfigurationResource {
  resourceId: string;
  declarationIndex: number;
  label: string;
  revision: number;
  isSaved: boolean;
  shape: 'missing' | 'file' | 'directory';
  fileCount: number;
  relativePath: string;
  syntax: 'json' | 'jsonWithComments' | 'yaml' | null;
}
export interface ConfigurationOverview {
  bindingId: string;
  bindingRevision: number;
  scriptId: string;
  scriptRevision: number;
  kind: 'generic' | 'specialized';
  extensionId: string | null;
  resources: ConfigurationResource[];
}
export function configurationOverview(value: unknown): ConfigurationOverview {
  const row = objectValue(value);
  const kind = stringValue(row.kind);
  if (kind !== 'generic' && kind !== 'specialized') throw new TypeError('configuration_kind_invalid');
  return {
    bindingId: stringValue(row.bindingId),
    bindingRevision: integerValue(row.bindingRevision),
    scriptId: stringValue(row.scriptId),
    scriptRevision: integerValue(row.scriptRevision),
    kind,
    extensionId: row.extensionId == null ? null : stringValue(row.extensionId),
    resources: arrayValue(row.resources, (item) => {
      const resource = objectValue(item);
      const shape = stringValue(resource.shape);
      if (shape !== 'missing' && shape !== 'file' && shape !== 'directory')
        throw new TypeError('configuration_shape_invalid');
      const syntax = resource.syntax == null ? null : stringValue(resource.syntax);
      if (syntax !== null && syntax !== 'json' && syntax !== 'jsonWithComments' && syntax !== 'yaml')
        throw new TypeError('configuration_syntax_invalid');
      return {
        resourceId: stringValue(resource.resourceId),
        declarationIndex: integerValue(resource.declarationIndex),
        label: stringValue(resource.label),
        revision: integerValue(resource.revision),
        isSaved: booleanValue(resource.isSaved),
        shape,
        fileCount: integerValue(resource.fileCount),
        relativePath: stringValue(resource.relativePath),
        syntax
      };
    })
  };
}
