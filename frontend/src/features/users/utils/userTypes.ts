export interface BindingEffective {
  enabled?: boolean;
  notifyEnabled?: boolean;
  smtpTo?: string;
  preRunScript?: string;
  postRunScript?: string;
  runDays?: number;
  maxSuccessfulRunsPerDay?: number;
  preRunOnceOnly?: boolean;
  postRunOnFinalOnly?: boolean;
}

export interface BindingLocks {
  general?: boolean;
  notification?: boolean;
  advanced?: boolean;
}

export interface Binding {
  scriptInstanceId: string;
  scriptName?: string;
  enabled?: boolean;
  notifyEnabled?: boolean;
  smtpTo?: string;
  preRunScript?: string;
  preRunOnceOnly?: boolean;
  postRunScript?: string;
  postRunOnFinalOnly?: boolean;
  runDays?: number;
  maxSuccessfulRunsPerDay?: number;
  configInputs?: Record<string, unknown>;
  effective?: BindingEffective;
  locks?: BindingLocks;
  pluginType?: string;
}

export interface User {
  id: string;
  index?: number;
  name: string;
  remark?: string;
  avatarUrl?: string;
  bindingCount?: number;
  nextRunAt?: string;
  nextQueueName?: string;
  bindings?: Binding[];
}

export interface Script {
  id: string;
  name: string;
  index?: number;
  pluginType?: string;
}

export interface Plugin {
  name?: string;
  displayName?: string;
  kind?: string;
  configuredEnabled?: boolean;
  runtimeEnabled?: boolean;
  state?: string;
}

export interface Badge {
  pluginName?: string;
  id?: string;
  label?: string;
  tone?: string;
  title?: string;
}

export interface UserBadges {
  userId?: string;
  badges?: Badge[];
}
