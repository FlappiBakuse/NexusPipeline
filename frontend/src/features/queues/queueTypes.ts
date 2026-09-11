import type { NxpOption } from "../../ui/primitives/NxpSelect.vue";

export type QueueTranslator = (
  key: string,
  args?: Record<string, unknown>,
  fallback?: string,
) => string;

export interface Script {
  id: string;
  name: string;
  pluginType?: string;
  logStallTimeoutMinutes?: number;
  users?: Array<{ enabled?: boolean }>;
}

export interface QueueTask {
  id?: string;
  index: number;
  scriptInstanceId: string;
}

export interface TimeSet {
  id?: string;
  enabled: boolean;
  days: number[];
  time: string;
}

export interface Queue {
  id: string;
  name: string;
  autoRunMode?: string;
  completionAction?: string;
  notifyEnabled?: boolean;
  tasks?: QueueTask[];
  timeSets?: TimeSet[];
  nextTrigger?: string;
}

export interface Plugin {
  name?: string;
  displayName?: string;
  kind?: string;
  configuredEnabled?: boolean;
  runtimeEnabled?: boolean;
  state?: string;
}

export interface QueueDraft {
  id: string;
  name: string;
  autoRunMode: string;
  completionAction: string;
  notifyEnabled: boolean;
  timeSets: TimeSet[];
  tasks: QueueTask[];
}

export interface QueuePluginIssue {
  tone: "bad" | "warn";
  title: string;
  label: string;
}

export interface QueueEditorOptions {
  mode: NxpOption[];
  completion: NxpOption[];
  scripts: NxpOption[];
}
