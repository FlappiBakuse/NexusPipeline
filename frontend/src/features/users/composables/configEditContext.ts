import type { InjectionKey, Ref } from "vue";
import type { useConfigEditFlow } from "./useConfigEditFlow";

export const configEditContextKey: InjectionKey<{
  flow: Ref<{ open: ReturnType<typeof useConfigEditFlow>["open"]; isOpen: boolean } | null>;
  completed: Ref<{ userId: string; revision: number } | null>;
}> = Symbol("configuration edit");
