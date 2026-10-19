import { computed, onBeforeUnmount, onMounted, ref } from "vue";
import { getCapabilities, type AccessDenyReason, type ClientCapabilities, type OperationAccess } from "./client-capabilities";
import { onServiceTrafficChanged } from "./service-traffic";
import { t } from "./i18n";

export function useOperationAccess(operation: OperationAccess) {
  const capabilities = ref<Readonly<ClientCapabilities> | null>(null);
  const controller = new AbortController();
  let requestId = 0;
  async function load() {
    const id = ++requestId;
    try { const value = await getCapabilities(controller.signal); if (id === requestId) capabilities.value = value; }
    catch { if (id === requestId) capabilities.value = null; }
  }
  const unsubscribe = onServiceTrafficChanged(paused => { requestId++; capabilities.value = null; if (!paused) void load(); });
  onMounted(() => void load());
  onBeforeUnmount(() => { requestId++; controller.abort(); unsubscribe(); });
  return {
    allowed: computed(() => operation === "general" || capabilities.value?.operations[operation].allowed === true),
    reason: computed(() => operation === "general" ? "" : accessReason(capabilities.value?.operations[operation].denyReason ?? "client_origin_unverified")),
  };
}
export function accessReason(reason: AccessDenyReason): string { return t(`api.error.${reason}`); }
