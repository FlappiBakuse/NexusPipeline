import type { AutomationBundle } from '../control/wire.generated';

export type ScriptDraft = AutomationBundle['scripts'][number];
export type UserDraft = AutomationBundle['users'][number];
export type QueueDraft = AutomationBundle['queues'][number];
export type AutomationEntity = ScriptDraft | UserDraft | QueueDraft;
type Mutable<T> = { -readonly [K in keyof T]: T[K] };
export type AutomationDraft = Partial<Mutable<ScriptDraft & UserDraft & QueueDraft>>;
