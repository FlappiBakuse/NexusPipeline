import { getCurrentInstance, inject, useSlots, type ComponentInternalInstance, type InjectionKey } from "vue";

export const PUBLIC_SLOT_PRESENCE: InjectionKey<(instance: ComponentInternalInstance | null, name: string) => boolean> = Symbol("public-slot-presence");

export function useSlotPresence() {
  const slots = useSlots();
  const instance = getCurrentInstance();
  const publicSlot = inject(PUBLIC_SLOT_PRESENCE, () => false);
  return (name: string) => Boolean(slots[name]) || publicSlot(instance, name);
}
