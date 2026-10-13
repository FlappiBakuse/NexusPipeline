import { onBeforeUnmount, shallowRef } from "vue";

export function useConfirmation() {
  const message = shallowRef("");
  let settle: ((accepted: boolean) => void) | undefined;
  function resolve(accepted: boolean) {
    const current = settle;
    settle = undefined;
    message.value = "";
    current?.(accepted);
  }
  function confirm(text: string): Promise<boolean> {
    if (settle) return Promise.resolve(false);
    message.value = text;
    return new Promise<boolean>(accept => { settle = accept; });
  }
  onBeforeUnmount(() => resolve(false));
  return { message, confirm, resolve };
}
