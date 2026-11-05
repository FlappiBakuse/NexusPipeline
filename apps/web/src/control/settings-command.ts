import { integerValue, objectValue } from './client';
import type { ControlHostSettings } from './wire.generated';

export function sameValue(left: unknown, right: unknown): boolean {
  if (left === right) return true;
  if (Array.isArray(left) || Array.isArray(right))
    return (
      Array.isArray(left) &&
      Array.isArray(right) &&
      left.length === right.length &&
      left.every((value, index) => sameValue(value, right[index]))
    );
  if (!left || !right || typeof left !== 'object' || typeof right !== 'object') return false;
  const first = left as Record<string, unknown>,
    second = right as Record<string, unknown>;
  const keys = Object.keys(first);
  return (
    keys.length === Object.keys(second).length &&
    keys.every((key) => Object.hasOwn(second, key) && sameValue(first[key], second[key]))
  );
}

export function savedSettings(value: unknown, body: ControlHostSettings): Record<string, unknown> {
  const response = objectValue(value),
    saved = objectValue(response.saved);
  if (
    response.operationId !== body.operationId ||
    integerValue(saved.revision) !== body.expectedRevision + 1 ||
    !sameValue(saved.values, body.values)
  )
    throw new TypeError('settings_result_unbound');
  return response;
}

export function completedSettings(value: unknown, body: ControlHostSettings): boolean {
  const operation = objectValue(value);
  return (
    operation.operationId === body.operationId &&
    operation.kind === 'settings.save' &&
    operation.status === 'completed'
  );
}
