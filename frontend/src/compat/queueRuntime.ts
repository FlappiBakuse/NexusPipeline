import { scriptPluginStatus, scriptPluginUnavailableMessage } from "@legacy/core/format.js";
import { state } from "@legacy/core/state.js";
import { disposePluginSlot } from "@legacy/core/plugin-runtime.js";
import { renderPluginSlot } from "@legacy/core/plugin-slots.js";

export interface QueueRuntimeLimits {
  maxQueues?: number;
  maxTimeSetsPerQueue?: number;
  maxQueueTotalUsers?: number;
}

export function queueRuntimeLimits(): QueueRuntimeLimits {
  return (state as { limits?: QueueRuntimeLimits }).limits || {};
}

export { disposePluginSlot, renderPluginSlot, scriptPluginStatus, scriptPluginUnavailableMessage };
