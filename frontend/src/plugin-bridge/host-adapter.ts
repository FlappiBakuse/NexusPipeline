// @ts-nocheck
/**
 * 平台访问集中在此适配层，避免公共 Frontend API 依赖宿主平台模块的内部布局。
 */
import { api, apiBlob, apiUpload, isAbortError } from "../platform/api";
export { getCapabilities } from "../platform/client-capabilities";
import {
  formatDate,
  formatList,
  formatNumber,
  formatTime,
  getLocale,
  onLocaleChanged,
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
  onLocaleChanged,
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

export { openExternal } from "../platform/navigation";
export {getClientSession} from '../platform/client-sessions';
export {openBrowserLogin} from '../platform/browser-login';
