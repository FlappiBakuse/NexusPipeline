import type { SurfaceContext } from '@nexuspipeline/extension-sdk';

export interface ProjectDraftBinding {
  userId: string;
  scriptId: string;
  scriptRevision: number;
  candidateId?: string;
}
interface Owner {
  read(): ProjectDraftBinding;
  stage(candidate: string): void;
}
const owners = new Map<string, Owner>();
const key = (context: SurfaceContext, extensionId: string) => JSON.stringify(
  [context.instanceId, context.surfaceId, context.entityId, extensionId]);

export function registerProjectDraft(context: SurfaceContext, extensionId: string, owner: Owner): () => void {
  const id = key(context, extensionId);
  owners.set(id, owner);
  return () => { if (owners.get(id) === owner) owners.delete(id); };
}

export function projectDraft(context: SurfaceContext, extensionId: string): Owner | undefined {
  return owners.get(key(context, extensionId));
}
