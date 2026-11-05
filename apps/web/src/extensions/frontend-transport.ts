import type { FrontendTransport, FrontendImage, FrontendPreview, SurfaceContext } from '@nexuspipeline/extension-sdk';
import { ControlFailure, ControlClient, objectValue, stringValue } from '../control/client';
import type { MediaPool } from '../control/media-pool';

export class InstalledTransport implements FrontendTransport {
  private readonly listeners = new Set<() => void>();
  private readonly base: string;

  constructor(
    readonly connectionKind: 'local' | 'remote',
    private readonly extensionId: string,
    activationId: string,
    private readonly client: ControlClient,
    private readonly media: MediaPool,
    private readonly signal: AbortSignal
  ) {
    this.base = `/control/v1/extensions/${extensionId}/frontend/${activationId}`;
  }

  async invoke(operation: string, input: unknown, signal: AbortSignal, operationId?: string): Promise<unknown> {
    this.require(operation);
    const active = AbortSignal.any([this.signal, signal]);
    active.throwIfAborted();
    const path = this.base + '/invoke/' + operation;
    const value = operationId
      ? await this.client.write(path, { operationId, input }, value => value, operationId, active)
      : await this.client.query(path, { input }, value => value, active);
    active.throwIfAborted();
    if (operationId) this.notify();
    return value;
  }

  async project(context: SurfaceContext, action: 'context' | 'discover' | 'select' | 'result' | 'preview' | 'page' | 'release',
    input: unknown, signal: AbortSignal, operationId?: string): Promise<unknown> {
    if (!['context', 'discover', 'select', 'result', 'preview', 'page', 'release'].includes(action) ||
        (action === 'select') !== Boolean(operationId)) throw new Error('project_operation_invalid');
    const active = AbortSignal.any([this.signal, signal]); active.throwIfAborted();
    const path = this.base + '/project/' + action;
    const body = { context, input, ...(operationId ? { operationId } : {}) };
    const result = operationId ? await this.client.write(path, body, value => value, operationId, active) :
      await this.client.query(path, body, value => value, active);
    active.throwIfAborted(); if (operationId) this.notify(); return result;
  }

  image(operation: string, assetId: string, signal: AbortSignal): Promise<FrontendImage> {
    this.require(operation);
    if (!/^[a-zA-Z0-9-]{1,128}$/.test(assetId)) throw new Error('extension_asset_identity_invalid');
    return this.media.acquire(this.base + '/images/' + operation + '/' + assetId,
      AbortSignal.any([this.signal, signal]));
  }

  async upload(operation: string, file: File, signal: AbortSignal, operationId: string): Promise<unknown> {
    this.require(operation);
    const active = AbortSignal.any([this.signal, signal]);
    const result = await this.client.upload(this.base + '/upload/' + operation, file, operationId, active);
    active.throwIfAborted(); this.notify(); return result;
  }

  async preview(context: SurfaceContext, providerId: string, signal: AbortSignal): Promise<FrontendPreview> {
    this.require(providerId);
    const active = AbortSignal.any([this.signal, signal]);
    active.throwIfAborted();
    if (!context.runId || !context.attemptId) return { state: 'waiting_run' };
    const grant = await this.client.write('/control/v1/media/previews', {
      format: 'nexus.control.media-grant', version: 1, runId: context.runId, attemptId: context.attemptId,
      extensionId: this.extensionId, providerId
    }, objectValue);
    const previewId = stringValue(grant.previewId);
    let image: FrontendImage | undefined;
    try {
      active.throwIfAborted();
      if (grant.runId !== context.runId || grant.attemptId !== context.attemptId)
        throw new Error('media_preview_identity_invalid');
      image = await this.media.acquire('/control/v1/media/previews/' + previewId + '/frame', active);
      active.throwIfAborted();
      if (!image.capturedAt || !Number.isFinite(Date.parse(image.capturedAt)) || image.height > 360)
        throw new Error('media_preview_response_invalid');
      return { state: 'ready', capturedAt: image.capturedAt, image: { ...image,
        ...(grant.source === 'pc_game' || grant.source === 'emulator' ? { source: grant.source } : {}) } };
    } catch (error) {
      image?.release();
      active.throwIfAborted();
      if (error instanceof ControlFailure && error.status === 409) {
        const state = previewStates[error.code];
        if (state) return { state };
      }
      throw error;
    } finally {
      try { await this.client.write('/control/v1/media/previews/' + previewId + '/close', null, objectValue); }
      catch (error) { image?.release(); throw error; }
    }
  }

  subscribe(listener: () => void): () => void {
    this.signal.throwIfAborted();
    this.listeners.add(listener);
    return () => { this.listeners.delete(listener); };
  }

  notify(): void {
    if (!this.signal.aborted) for (const listener of this.listeners) listener();
  }

  private require(operation: string): void {
    this.signal.throwIfAborted();
    if (!/^[a-z][a-z0-9-]{0,79}$/.test(operation)) throw new Error('extension_operation_invalid');
  }
}

const previewStates: Record<string, Exclude<FrontendPreview['state'], 'ready'> | undefined> = {
  media_waiting_for_game: 'waiting_for_game',
  media_window_not_ready: 'window_not_ready',
  media_emulator_not_ready: 'emulator_not_ready',
  media_preview_unavailable: 'unavailable'
};
