// @ts-nocheck
/**
 * 插件桥接层的宿主依赖边界。
 *
 * 桥接实现只通过本文件访问宿主平台服务，不直接 import 平台目录、宿主 feature 或
 * 宿主 UI 内部实现。平台模块逐个迁移时只需要改这里的实现来源，桥接层与 Frontend API
 * 1.5 的外部契约保持不变。
 *
 * 平台迁移完成后本文件不再引用前端目录之外的源码。
 */
import { api, apiBlob, apiUpload, isAbortError } from "../platform/api";
import {
  formatDate,
  formatList,
  formatNumber,
  formatTime,
  getLocale,
  t,
} from "../platform/i18n";
import { clearFieldError, setRequiredFieldError, toast } from "../platform/toast";
import {
  createAppearanceHost,
} from "../platform/appearance";
import { captureExecutionPreview } from "../platform/execution-preview";
import {
  disposePage,
  enterPage,
  registerInterval,
  releaseController,
  state,
  trackController,
} from "../platform/page-state";

export {
  api,
  apiBlob,
  apiUpload,
  isAbortError,
  formatDate,
  formatList,
  formatNumber,
  formatTime,
  getLocale,
  t,
  clearFieldError,
  setRequiredFieldError,
  toast,
  createAppearanceHost,
  captureExecutionPreview,
  disposePage,
  enterPage,
  registerInterval,
  releaseController,
  state,
  trackController,
};

export interface PluginBridgeApiClient {
  (method: string, path: string, body?: unknown, signal?: AbortSignal): Promise<unknown>;
}
