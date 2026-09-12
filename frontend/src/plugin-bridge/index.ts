/**
 * 宿主侧插件桥接 facade。
 *
 * `frontend/src/app/**`、`frontend/src/features/**` 与 `frontend/src/ui/**` 只能通过本入口
 * 使用插件能力，不直接引用 `plugin-bridge/` 内部实现。插件的公开契约是 Frontend API 1.5、
 * 18 个公开 slot、公开 `nxp-*` Native Custom Elements 与可观察的 slot/生命周期语义。
 */
import { disposePluginSlot, initPluginRuntime } from "./runtime";
import { renderPluginSlot } from "./slots";
import { installPluginControlEvents } from "./controls";

installPluginControlEvents();

export {
  disposePluginSlot,
  initPluginRuntime,
  renderPluginSlot,
};

export {
  notifyPluginDispose,
  notifyPluginPageEnter,
  notifyPluginPageLeave,
  notifyPluginPageUpdated,
  pluginRuntimeStatus,
  refreshPluginRuntime,
  resolvePluginRoute,
  syncPluginNavActive,
} from "./runtime";

export { FRONTEND_API_VERSION, PLUGIN_SLOT_NAMES, verifyPluginApiVersion } from "./runtime";

export * from "./types";
