export interface QueueRuntimeLimits {
  maxQueues?: number;
  maxTimeSetsPerQueue?: number;
  maxQueueTotalUsers?: number;
}

export interface PluginSlotContext {
  mode?: string;
  primaryId?: string;
  secondaryId?: string;
  [key: string]: unknown;
}



export type { ClientCapabilities, ConnectionKind, OperationAccess, AccessDecision, AccessDenyReason } from "../platform/client-capabilities";

export interface PluginFieldOption {
  value?: string;
  label?: string;
  disabled?: boolean;
  title?: string;
}

export interface PluginField {
  key?: string;
  label?: string;
  type?: string;
  required?: boolean;
  readOnly?: boolean;
  description?: string;
  placeholder?: string;
  maxLength?: number;
  min?: number | string;
  max?: number | string;
  step?: number | string;
  options?: Array<PluginFieldOption | string>;
}

export interface PluginUiContribution {
  id?: string;
  pluginName?: string;
  pluginDisplayName?: string;
  kind?: string;
  title?: string;
  description?: string;
  order?: number;
  context?: PluginSlotContext;
  fields?: PluginField[];
  values?: Record<string, unknown>;
  [key: string]: unknown;
}

export interface DashboardCardSurface {
  element: HTMLElement;
  signal: AbortSignal;
  cardId: string;
  context: Readonly<Record<string, unknown>>;
}
export type DashboardCardRenderer = (surface: DashboardCardSurface) => void | (() => void) | Promise<void | (() => void)>;
