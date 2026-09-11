import { state } from "./page-state";

export interface QueueRuntimeLimits {
  maxQueues?: number;
  maxTimeSetsPerQueue?: number;
  maxQueueTotalUsers?: number;
}

/** 队列数量与子项上限来自 shell 启动时加载的宿主约束，页面只读取当前快照。 */
export function queueRuntimeLimits(): QueueRuntimeLimits {
  return (state as { limits?: QueueRuntimeLimits }).limits || {};
}
