export function copyDraft<T>(value: T): T {
  return JSON.parse(JSON.stringify(value)) as T;
}
