export interface LogPartMeta {
  index: number;
  source: string;
  fileSourceId: number | null;
  fileOffset: number | null;
  sizeBytes: number;
}

export function neighborLogPart(
  parts: readonly LogPartMeta[],
  position: number,
  direction: -1 | 1
): LogPartMeta | undefined {
  const current = parts[position];
  if (!current) return undefined;
  for (let offset = position + direction; offset >= 0 && offset < parts.length; offset += direction) {
    const candidate = parts[offset];
    if (candidate.source !== current.source || candidate.fileSourceId !== current.fileSourceId) continue;
    if (current.source === 'file') {
      if (current.fileOffset === null || candidate.fileOffset === null) return undefined;
      const previous = direction === -1 ? candidate : current;
      const next = direction === -1 ? current : candidate;
      if (previous.fileOffset! + previous.sizeBytes !== next.fileOffset) return undefined;
    }
    return candidate;
  }
  return undefined;
}

export function decodeLogPart(bytes: Uint8Array, previousTail: Uint8Array, hasContinuation: boolean): string {
  const decoder = new TextDecoder('utf-8');
  if (previousTail.length) decoder.decode(previousTail, { stream: true });
  return decoder.decode(bytes, { stream: hasContinuation });
}

export async function trailingLogBytes(
  response: Response,
  maximum: number,
  signal: AbortSignal
): Promise<Uint8Array> {
  const reader = response.body?.getReader();
  if (!reader) return new Uint8Array();
  let total = 0;
  let tail = new Uint8Array();
  try {
    while (true) {
      signal.throwIfAborted();
      const next = await reader.read();
      if (next.done) return tail;
      total += next.value.byteLength;
      if (total > maximum) throw Error('control_response_limit');
      const size = Math.min(3, tail.length + next.value.length);
      const updated = new Uint8Array(size);
      const prior = Math.max(0, size - next.value.length);
      if (prior) updated.set(tail.subarray(tail.length - prior));
      updated.set(next.value.subarray(next.value.length - (size - prior)), prior);
      tail = updated;
    }
  } finally {
    await reader.cancel().catch(() => {});
    reader.releaseLock();
  }
}
