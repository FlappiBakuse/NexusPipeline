import documents from './schemas.generated';
export type Schema = {
  $ref?: string;
  type?: string | string[];
  properties?: Record<string, Schema>;
  required?: string[];
  items?: Schema;
  enum?: unknown[];
  const?: unknown;
  default?: unknown;
  minimum?: number;
  maximum?: number;
  minLength?: number;
  maxLength?: number;
  minItems?: number;
  maxItems?: number;
  oneOf?: Schema[];
  $defs?: Record<string, Schema>;
  additionalProperties?: boolean;
  pattern?: string;
  uniqueItems?: boolean;
};
const all = documents as unknown as Record<string, Schema>;
export function schemaAt(reference: string, document = ''): Schema {
  const [name, pointer] = reference.split('#');
  let node: unknown = all[name || document];
  for (const segment of (pointer || '').split('/').filter(Boolean)) {
    node = (node as Record<string, unknown>)[segment.replace(/~1/g, '/').replace(/~0/g, '~')];
  }
  if (!node || typeof node !== 'object') throw new Error('schema_reference_invalid');
  return resolveSchema(node as Schema, name || document);
}
export function resolveSchema(schema: Schema, document: string): Schema {
  if (schema.$ref) return schemaAt(schema.$ref, document);
  if (schema.properties)
    return {
      ...schema,
      properties: Object.fromEntries(
        Object.entries(schema.properties).map(([name, value]) => [
          name,
          value.$ref ? schemaAt(value.$ref, document) : resolveSchema(value, document)
        ])
      )
    };
  if (schema.items) return { ...schema, items: resolveSchema(schema.items, document) };
  if (schema.oneOf) return { ...schema, oneOf: schema.oneOf.map((item) => resolveSchema(item, document)) };
  return schema;
}
export function initialValue(schema: Schema): unknown {
  if (schema.default !== undefined) return structuredClone(schema.default);
  if (schema.const !== undefined) return schema.const;
  if (schema.enum?.length) return schema.enum[0];
  if (schema.oneOf?.length) return initialValue(schema.oneOf[0]);
  if (schema.type === 'array') return [];
  if (schema.type === 'object')
    return Object.fromEntries(
      (schema.required || []).map((name) => [name, initialValue(schema.properties?.[name] || {})])
    );
  if (schema.type === 'boolean') return false;
  if (schema.type === 'integer' || schema.type === 'number') return schema.minimum || 0;
  if (Array.isArray(schema.type) && schema.type.includes('null')) return null;
  return '';
}

export function matchesSchema(value: unknown, schema: Schema, depth = 0): boolean {
  if (depth > 64) return false;
  if (schema.const !== undefined && value !== schema.const) return false;
  if (schema.enum && !schema.enum.includes(value)) return false;
  if (schema.oneOf && schema.oneOf.filter((item) => matchesSchema(value, item, depth + 1)).length !== 1)
    return false;
  const type = value === null ? 'null' : Array.isArray(value) ? 'array' : typeof value;
  const types = schema.type ? (Array.isArray(schema.type) ? schema.type : [schema.type]) : [];
  if (
    types.length &&
    !types.includes(type) &&
    !(type === 'number' && Number.isInteger(value) && types.includes('integer'))
  )
    return false;
  if (typeof value === 'number')
    return (
      Number.isFinite(value) &&
      (schema.minimum === undefined || value >= schema.minimum) &&
      (schema.maximum === undefined || value <= schema.maximum)
    );
  if (typeof value === 'string')
    return (
      (schema.minLength === undefined || value.length >= schema.minLength) &&
      (schema.maxLength === undefined || value.length <= schema.maxLength) &&
      (!schema.pattern || new RegExp(schema.pattern).test(value))
    );
  if (Array.isArray(value))
    return (
      (schema.minItems === undefined || value.length >= schema.minItems) &&
      (schema.maxItems === undefined || value.length <= schema.maxItems) &&
      (!schema.uniqueItems || new Set(value.map((item) => JSON.stringify(item))).size === value.length) &&
      (!schema.items || value.every((item) => matchesSchema(item, schema.items!, depth + 1)))
    );
  if (value && typeof value === 'object') {
    const record = value as Record<string, unknown>;
    if (schema.required?.some((key) => !Object.hasOwn(record, key))) return false;
    return Object.entries(record).every(([key, item]) =>
      schema.properties?.[key]
        ? matchesSchema(item, schema.properties[key]!, depth + 1)
        : schema.additionalProperties !== false
    );
  }
  return true;
}
