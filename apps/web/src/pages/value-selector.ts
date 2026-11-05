export function valueSelector(document: unknown, pointer: string): (string | number)[] {
  if (!pointer.startsWith('/')) throw new Error('configuration_selector_invalid');
  const parts = pointer.slice(1).split('/');
  if (parts.length > 64 || parts.some((part) => part.length > 256 || /~(?![01])/u.test(part)))
    throw new Error('configuration_selector_invalid');
  const result: (string | number)[] = [];
  let container = document;
  for (const encoded of parts) {
    const key = encoded.replace(/~1/g, '/').replace(/~0/g, '~');
    if (Array.isArray(container)) {
      if (
        !/^(0|[1-9]\d*)$/.test(key) ||
        !Number.isSafeInteger(Number(key)) ||
        Number(key) >= container.length
      )
        throw new Error('configuration_selector_invalid');
      const index = Number(key);
      result.push(index);
      container = container[index];
    } else if (container && typeof container === 'object' && Object.hasOwn(container, key)) {
      result.push(key);
      container = (container as Record<string, unknown>)[key];
    } else throw new Error('configuration_selector_invalid');
  }
  return result;
}
