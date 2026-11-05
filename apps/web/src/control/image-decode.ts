interface ImageCodec {
  decode(options: { frameIndex: number; completeFramesOnly: boolean }): Promise<{ image: VideoFrame }>;
  close(): void;
}
interface CodecFactory {
  new (options: { type: string; data: ArrayBuffer; desiredWidth: number; desiredHeight: number;
    preferAnimation: boolean; transfer: ArrayBuffer[] }): ImageCodec;
  isTypeSupported(type: string): Promise<boolean>;
}

export async function drawImage(blob: Blob, surface: HTMLCanvasElement, width: number, height: number,
  signal: AbortSignal): Promise<void> {
  signal.throwIfAborted();
  if (!Number.isSafeInteger(width) || !Number.isSafeInteger(height) || width < 1 || height < 1 ||
      width > 8192 || height > 8192 || width * height > 33554432) throw new Error('image_decode_dimensions_invalid');
  const factory = (globalThis as typeof globalThis & { ImageDecoder?: CodecFactory }).ImageDecoder;
  let image: VideoFrame | ImageBitmap | undefined;
  let codec: ImageCodec | undefined;
  const abort = () => codec?.close();
  const release = await reserveDecode(width * height * 4, signal);
  try {
    if (factory && await factory.isTypeSupported(blob.type)) {
      signal.throwIfAborted();
      const data = await blob.arrayBuffer();
      signal.throwIfAborted();
      codec = new factory({ type: blob.type, data, desiredWidth: width, desiredHeight: height,
        preferAnimation: false, transfer: [data] });
      signal.addEventListener('abort', abort, { once: true });
      image = (await codec.decode({ frameIndex: 0, completeFramesOnly: true })).image;
    } else {
      signal.throwIfAborted();
      image = await createImageBitmap(blob, { resizeWidth: width, resizeHeight: height, resizeQuality: 'high' });
    }
    signal.throwIfAborted();
    surface.width = width;
    surface.height = height;
    const context = surface.getContext('2d', { alpha: true, willReadFrequently: true });
    if (!context) throw new Error('image_surface_unavailable');
    context.imageSmoothingEnabled = true;
    context.imageSmoothingQuality = 'high';
    context.drawImage(image, 0, 0, width, height);
  } finally {
    signal.removeEventListener('abort', abort);
    try { image?.close(); codec?.close(); }
    finally { release(); }
  }
}

const decodeLimit = 64 * 1024 * 1024;
let activeDecodes = 0, decodedBytes = 0;
const decodeQueue: { bytes: number; signal: AbortSignal; enter(): void; cancel(): void }[] = [];

export function decodeMetrics(): Readonly<{ active: number; estimatedBytes: number; queued: number }> {
  return { active: activeDecodes, estimatedBytes: decodedBytes, queued: decodeQueue.length };
}

function reserveDecode(bytes: number, signal: AbortSignal): Promise<() => void> {
  signal.throwIfAborted();
  if (decodeQueue.length >= 256) return Promise.reject(new Error('image_decode_queue_limit'));
  return new Promise((resolve, reject) => {
    const item = {
      bytes, signal,
      enter() {
        signal.removeEventListener('abort', item.cancel);
        activeDecodes++; decodedBytes += bytes;
        let released = false;
        resolve(() => {
          if (released) return;
          released = true; activeDecodes--; decodedBytes -= bytes; drainDecodes();
        });
      },
      cancel() {
        const index = decodeQueue.indexOf(item);
        if (index >= 0) decodeQueue.splice(index, 1);
        signal.removeEventListener('abort', item.cancel);
        reject(signal.reason); drainDecodes();
      }
    };
    decodeQueue.push(item);
    signal.addEventListener('abort', item.cancel, { once: true });
    if (signal.aborted) item.cancel(); else drainDecodes();
  });
}

function drainDecodes(): void {
  while (decodeQueue.length && activeDecodes < 2) {
    const next = decodeQueue[0]!;
    if (next.signal.aborted) { next.cancel(); continue; }
    // A single original above the target budget runs alone so original-resolution access remains available.
    if (decodedBytes + next.bytes > decodeLimit && activeDecodes !== 0) return;
    decodeQueue.shift(); next.enter();
  }
}
