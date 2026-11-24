import { boundedInteger, enumValue, objectValue, stringValue, type ControlClient } from '../control/client';
import type { HistoryLog } from './attempt-resources';

interface HistoryLogPart {
  source: string;
  fileOffset: number | null;
  size: number;
  continues: boolean;
}
const textSegmentLimit = 131072;

export async function readHistoryLog(
  client: ControlClient, runId: string, attemptId: string, log: HistoryLog, signal: AbortSignal
): Promise<readonly string[]> {
  const { stepId, role, invocationOrdinal } = log.key;
  const base = '/control/v1/records/' + runId + '/attempts/' + attemptId + '/logs/' +
    encodeURIComponent(stepId) + '/' + encodeURIComponent(role);
  const query = new URLSearchParams();
  if (invocationOrdinal) query.set('invocationOrdinal', String(invocationOrdinal));
  const description = await client.read(base + '/description' + (query.size ? '?' + query : ''), objectValue, signal);
  signal.throwIfAborted();
  const key = objectValue(description.key);
  const digest = stringValue(description.snapshotSha256);
  if (key.runId !== runId || key.attemptId !== attemptId || key.stepId !== stepId || key.role !== role ||
      (key.invocationOrdinal ?? null) !== (invocationOrdinal ?? null) || !/^[a-f0-9]{64}$/.test(digest))
    throw Error('history_log_identity_invalid');
  query.set('snapshotSha256', digest);
  const size = boundedInteger(description.recordedBytes, 0, 64 * 1024 * 1024);
  const parts = readLogParts(description.parts, size);
  const response = await client.response(base + '/original?' + query, signal);
  signal.throwIfAborted();
  return await decodeLog(response, parts, size, signal);
}

function readLogParts(value: unknown, size: number): HistoryLogPart[] {
  if (!Array.isArray(value) || value.length > 4096) throw Error('history_log_parts_invalid');
  let total = 0;
  const previous = new Map<string, HistoryLogPart>();
  const parts = value.map((value: unknown, index): HistoryLogPart => {
    const item = objectValue(value);
    if (item.index !== index) throw Error('history_log_parts_invalid');
    const source = enumValue(item.source, ['standardOutput', 'standardError', 'file']);
    const size = boundedInteger(item.sizeBytes, 1, 65536);
    total += size;
    const file = source === 'file';
    if (!file && (item.fileOffset != null || item.fileSourceId != null)) throw Error('history_log_parts_invalid');
    const fileOffset = file ? boundedInteger(item.fileOffset, 0, Number.MAX_SAFE_INTEGER - size) : null;
    const key = file ? source + '/' + boundedInteger(item.fileSourceId, 0, 255) : source;
    const part = { source: key, fileOffset, size, continues: false };
    const prior = previous.get(key);
    if (prior) prior.continues = prior.fileOffset === null || prior.fileOffset + prior.size === fileOffset;
    previous.set(key, part);
    return part;
  });
  if (total !== size) throw Error('history_log_size_invalid');
  return parts;
}

async function decodeLog(response: Response, parts: HistoryLogPart[], size: number, signal: AbortSignal): Promise<readonly string[]> {
  const reader = response.body?.getReader();
  if (!reader) throw Error('log_stream_unavailable');
  const decoders = new Map<string, TextDecoder>();
  const segments: string[] = [];
  let text = '';
  let received = 0;
  let index = 0;
  let remaining = parts[index]?.size ?? 0;
  try {
    while (true) {
      const chunk = await reader.read();
      signal.throwIfAborted();
      if (chunk.done) break;
      received += chunk.value.byteLength;
      if (received > size) throw Error('history_log_size_invalid');
      for (let offset = 0; offset < chunk.value.byteLength;) {
        const part = parts[index]!;
        const length = Math.min(remaining, chunk.value.byteLength - offset);
        const end = offset + length;
        remaining -= length;
        // Transport chunks may span several archive parts from different sources.
        const stream = remaining > 0 || part.continues;
        const decoder = decoders.get(part.source) ?? new TextDecoder();
        const decoded = decoder.decode(chunk.value.subarray(offset, end), { stream });
        if (text.length + decoded.length > textSegmentLimit) {
          segments.push(text);
          text = decoded;
        } else text += decoded;
        if (stream) decoders.set(part.source, decoder);
        else decoders.delete(part.source);
        if (remaining === 0) remaining = parts[++index]?.size ?? 0;
        offset = end;
      }
    }
  } finally {
    void reader.cancel().catch(() => {});
    reader.releaseLock();
  }
  if (received !== size) throw Error('history_log_size_invalid');
  if (text) segments.push(text);
  return segments;
}
