export interface QueueRuntimeLimits {
  maxQueues?: number;
  maxTimeSetsPerQueue?: number;
  maxQueueTotalUsers?: number;
}

export interface PluginDescriptor {
  name: string;
  displayName?: string;
  entryUrl?: string;
  styleUrls?: string[];
  localization?: {
    defaultLocale?: string;
    locales?: Record<string, Record<string, string>>;
  };
  [key: string]: unknown;
}

export interface PluginSlotContext {
  mode?: string;
  primaryId?: string;
  secondaryId?: string;
  [key: string]: unknown;
}

export interface PluginSlotSurface {
  element: HTMLElement;
  context: Readonly<PluginSlotContext>;
}

export type PluginSlotRenderer = (surface: PluginSlotSurface) => unknown;

export type PluginCleanup = () => void;

export interface Disposable {
  dispose: () => void;
}

export interface PluginRouteHandler {
  (token: number, segments: string[], host?: unknown): unknown;
}

export interface PluginLifecyclePayload {
  hash: string;
  page: string;
  segments: string[];
  token?: number;
  container?: Element | null;
}

export interface PluginNavItem {
  id?: string;
  title?: string;
  route?: string;
  order?: number;
  icon?: string;
}

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
