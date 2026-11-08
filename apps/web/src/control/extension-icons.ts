import { shallowReactive } from 'vue';
import { boundedBytes, type ControlClient } from './client';
import generic from '../assets/script-icons/generic.ico';
import { abortable } from './abortable';

const icons = shallowReactive(new Map<string, string>());
let generation = 0;
export function extensionIcon(id?: string): string { return id ? icons.get(id) ?? generic : generic; }
export function clearExtensionIcons(): void {
  generation++;
  for (const url of icons.values()) URL.revokeObjectURL(url);
  icons.clear();
}
export async function prepareExtensionIcons(client: ControlClient, ids: readonly string[], signal: AbortSignal): Promise<void> {
  const own = ++generation;
  for (const [id, url] of icons) if (!ids.includes(id)) { URL.revokeObjectURL(url); icons.delete(id); }
  let next = 0;
  const errors: unknown[] = [];
  await Promise.all(Array.from({ length: Math.min(4, ids.length) }, async () => {
    while (next < ids.length && !signal.aborted && own === generation) {
      const id = ids[next++]!;
      let url: string | undefined;
      try {
        if (!/^[a-z][a-z0-9-]{0,79}$/.test(id)) throw new Error('extension_icon_identity_invalid');
        const response = await client.response('/control/v1/extensions/' + id + '/icon', signal);
        const bytes = await boundedBytes(response, 1048576);
        if (bytes.length) {
          const type = response.headers.get('Content-Type')?.split(';')[0];
          if (type !== 'image/png' && type !== 'image/x-icon') throw new Error('extension_icon_format_invalid');
          const blob = new Blob([bytes as BlobPart], { type });
          const decoding = createImageBitmap(blob).then(bitmap => {
            try {
              if (!bitmap.width || !bitmap.height || bitmap.width * bitmap.height > 16777216)
                throw new Error('extension_icon_dimensions_invalid');
            } finally { bitmap.close(); }
          });
          await abortable(decoding, signal);
          url = URL.createObjectURL(blob);
        }
        signal.throwIfAborted();
        if (own !== generation) return;
        const previous = icons.get(id);
        if (previous) URL.revokeObjectURL(previous);
        icons.delete(id);
        if (url) { icons.set(id, url); url = undefined; }
      } catch (error) { if (!signal.aborted && own === generation) errors.push(error); }
      finally { if (url) URL.revokeObjectURL(url); }
    }
  }));
  signal.throwIfAborted();
  if (errors.length) throw new AggregateError(errors, 'extension_icons_unavailable');
}
