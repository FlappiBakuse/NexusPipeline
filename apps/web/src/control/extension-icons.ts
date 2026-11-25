import { shallowReactive } from 'vue';
import { boundedBytes, type ControlClient } from './client';
import generic from '../assets/script-icons/generic.ico';
import { abortable } from './abortable';
import { retryRead } from './read-retry';
import type { ExtensionState } from './extensions';

const icons = shallowReactive(new Map<string, string>());
type IconRevision = Pick<ExtensionState, 'extensionId' | 'installedRevision'>;
type IconPreparation = {
  client: ControlClient;
  key: string;
  owner: AbortController;
  work: Promise<void>;
  readers: number;
  complete: boolean;
};
let preparation: IconPreparation | undefined;
export function extensionIcon(id?: string): string { return id ? icons.get(id) ?? generic : generic; }
export function clearExtensionIcons(): void {
  preparation?.owner.abort();
  preparation = undefined;
  for (const url of icons.values()) URL.revokeObjectURL(url);
  icons.clear();
}
export async function prepareExtensionIcons(client: ControlClient, revisions: readonly IconRevision[], signal: AbortSignal): Promise<void> {
  signal.throwIfAborted();
  const key = JSON.stringify(revisions.map(item => [item.extensionId, item.installedRevision]).sort());
  let current = preparation;
  if (!current || current.client !== client || current.key !== key) {
    current?.owner.abort();
    const owner = new AbortController();
    const work = loadIcons(client, revisions.map(item => item.extensionId), owner.signal);
    const started = { client, key, owner, work, readers: 0, complete: false };
    preparation = current = started;
    void work.then(() => { started.complete = true; }, () => {
      if (preparation === started) preparation = undefined;
    });
  }
  current.readers++;
  try { await abortable(current.work, signal); }
  finally {
    if (--current.readers === 0 && !current.complete && preparation === current) {
      preparation = undefined;
      current.owner.abort();
    }
  }
}

async function loadIcons(client: ControlClient, ids: readonly string[], signal: AbortSignal): Promise<void> {
  for (const [id, url] of icons) if (!ids.includes(id)) { URL.revokeObjectURL(url); icons.delete(id); }
  let next = 0;
  const errors: unknown[] = [];
  await Promise.all(Array.from({ length: Math.min(4, ids.length) }, async () => {
    while (next < ids.length && !signal.aborted) {
      const id = ids[next++]!;
      let url: string | undefined;
      try {
        if (!/^[a-z][a-z0-9-]{0,79}$/.test(id)) throw new Error('extension_icon_identity_invalid');
        const { bytes, type } = await retryRead(async () => {
          const response = await client.response('/control/v1/extensions/' + id + '/icon', signal);
          return { bytes: await boundedBytes(response, 1048576, signal),
            type: response.headers.get('Content-Type')?.split(';')[0] };
        }, signal);
        if (bytes.length) {
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
        const previous = icons.get(id);
        if (previous) URL.revokeObjectURL(previous);
        icons.delete(id);
        if (url) { icons.set(id, url); url = undefined; }
      } catch (error) { if (!signal.aborted) errors.push(error); }
      finally { if (url) URL.revokeObjectURL(url); }
    }
  }));
  signal.throwIfAborted();
  if (errors.length) throw errors[0];
}
