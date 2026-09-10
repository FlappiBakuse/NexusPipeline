import { api, hydrateIcons } from "../core/api.js";
import { $ as $dom } from "../core/dom.js";
import { esc, pluginDisplayName, scriptFallbackIcon, scriptPluginStatus, scriptPluginUnavailableMessage } from "../core/format.js";
import { pathField, selectField, switchControl, valueField, pageHeader } from "../core/forms.js";
import { icon } from "../core/icons.js";
import { pagerMarkup, registerPager, replacePageOrder } from "../core/pager.js";
import { isCurrent, state } from "../core/state.js";
import { closeModal, confirmModal, modalShell, showModal } from "../core/modal.js";
import { hasEntityNameConflict } from "../core/entity-name.js";
import { navActive, render, setFieldError, setFieldInvalid, setRequiredFieldError, clearFieldError, setTopbarTitle, toast, pushNotice, withBusy } from "../core/ui.js";
import { initDndList } from "../core/dnd.js";
import { pluginSlotMarkup, renderPluginSlots } from "../core/plugin-slots.js";
import { t } from "../core/i18n.js";

let scriptDraft = null;
let scriptPage = 1;
const SCRIPT_PAGE_SIZE = 20;
const MAX_ENTITY_NAME_BYTES = 64;

function specializedPlugins() {
  return (state.plugins || []).filter(p => p.kind === "data-specialized" && p.configuredEnabled && p.runtimeEnabled && p.state === "Active");
}

/** 启动方式选择是否可用：模拟器是宿主基础设施；专项脚本按插件 capability 声明（缺省不支持）。 */
function emulatorAllowed(pluginType) {
  if (!pluginType) return true;
  const normalized = String(pluginType).trim().toLowerCase();
  const meta = (state.plugins || []).find(p => String(p.name || "").trim().toLowerCase() === normalized);
  return !!meta && meta.kind === "data-specialized" && meta.configuredEnabled && meta.runtimeEnabled && meta.state === "Active" && !!meta.supportsEmulator;
}

/** 声明 self-managed-pc-launch 能力的插件：PC 客户端启动由脚本自身（含启动器）完成，外部不代填启动项。 */
function selfManagedPcLaunch(pluginType) {
  if (!pluginType) return false;
  const normalized = String(pluginType).trim().toLowerCase();
  const meta = (state.plugins || []).find(p => String(p.name || "").trim().toLowerCase() === normalized);
  return !!meta && meta.selfManagedPcLaunch === true;
}

/** 游戏配置卡：启动方式选择器（仅支持时渲染）+ ADB 地址/游戏路径按模式切换 + 启动参数 + 等待秒数。 */
function gameBoxHtml(d, emulatorOk) {
  const isEmu = emulatorOk && d.gameMode === "emulator";
  const modeRow = emulatorOk
    ? `<div class="form-grid game-mode-row">${selectField("sm-mode", t("scripts.startup_mode"), isEmu ? "emulator" : "pc", [{ value: "pc", label: t("scripts.pc_client") }, { value: "emulator", label: t("scripts.android_emulator") }], 'data-action="change-sm-mode"', t("scripts.select_game_start_mode"))}<div class="game-wait-field">${valueField("sm-game-wait", t("scripts.wait_after_game_start"), d.gameWaitSeconds, "number", 'min="0"', t("scripts.wait_after_game_start_help"))}</div></div>`
    : `<div class="form-grid game-mode-row">${valueField("sm-game-wait", t("scripts.wait_after_game_start"), d.gameWaitSeconds, "number", 'min="0"', t("scripts.wait_after_game_start_help"))}<div class="game-wait-field" aria-hidden="true"></div></div>`;
  const exeField = pathField(
    "sm-game-exe",
    isEmu ? `${t("scripts.emulator_adb_address")} <span class='req'>*</span>` : `${t("scripts.game_path")} <span class='req'>*</span>`,
    d.gameExe,
    "file",
    isEmu ? `placeholder="${t("scripts.editor.adb.placeholder")}"` : `placeholder="${t("scripts.editor.game_path.placeholder")}"`,
    t("common.executable_file_filter"),
    isEmu ? 'hidden aria-hidden="true"' : "",
    isEmu ? t("scripts.editor.adb_cleanup_help") : t("scripts.editor.game_path.cleanup_help"),
  );
  const argsField = isEmu
    ? valueField("sm-game-args", t("scripts.script_startup_arguments"), d.gameArgs, "text", `placeholder="${t("scripts.am_start_arguments")}"`, t("scripts.android.arguments_mode_help"))
    : valueField("sm-game-args", t("scripts.script_startup_arguments"), d.gameArgs);
  const selfManagedHint = !isEmu && selfManagedPcLaunch(d.pluginType)
    ? `<p id="sm-self-managed-hint" class="muted">${t("scripts.editor.launch.controlled_help")}</p>`
    : `<p id="sm-self-managed-hint" class="muted" hidden>${t("scripts.editor.launch.controlled_help")}</p>`;
  return `${selfManagedHint}<div class="form-grid">${exeField}${argsField}</div>${modeRow}`;
}

/** 启动方式切换：更新游戏路径/ADB 地址字段的标签与提示；self-managed-pc-launch 插件的 PC 模式禁用游戏启动项。 */
export function changeGameMode() {
  const isEmu = $dom("#sm-mode")?.value === "emulator";
  const exe = $dom("#sm-game-exe");
  const args = $dom("#sm-game-args");
  const exeLabel = $dom('label[for="sm-game-exe"]');
  if (exeLabel) exeLabel.innerHTML = `${t(isEmu ? "scripts.emulator_adb_address" : "scripts.game_path")} <span class='req'>*</span>`;
  if (exe) exe.placeholder = isEmu ? t("scripts.editor.adb.placeholder") : t("scripts.editor.game_path.placeholder");
  if (args) args.placeholder = isEmu ? t("scripts.android.arguments_help") : "";
  const pathTrigger = exe?.closest(".nxp-path")?.querySelector("[data-path-trigger]");
  if (pathTrigger) {
    pathTrigger.hidden = isEmu;
    pathTrigger.setAttribute("aria-hidden", isEmu ? "true" : "false");
    pathTrigger.disabled = isEmu;
    pathTrigger.dataset.pathTitle = t(isEmu ? "scripts.emulator_adb_address" : "scripts.game_path");
  }
  const lockPcFields = selfManagedPcLaunch(scriptDraft?.pluginType || "") && !isEmu;
  const currentMode = isEmu ? "emulator" : "pc";
  const previousMode = scriptDraft?._lastGameMode;
  const argsField = $dom("#sm-game-args");
  const waitField = $dom("#sm-game-wait");
  const launchButton = $dom("#sm-launch");
  if (scriptDraft && previousMode && previousMode !== currentMode) {
    if (currentMode === "pc") {
      // 从模拟器切回 PC 前保存当前草稿；PC self-managed 只暂时禁用这些设置。
      scriptDraft.gameArgs = argsField?.value.trim() || "";
      const parsedWait = Number.parseInt(waitField?.value || "", 10);
      if (Number.isFinite(parsedWait) && parsedWait >= 0) scriptDraft.gameWaitSeconds = parsedWait;
      scriptDraft.launchGame = launchButton?.getAttribute("aria-pressed") === "true";
    } else {
      // 从 PC 切到模拟器时恢复被暂时禁用的草稿值，保留用户原始启动设置。
      if (argsField) argsField.value = scriptDraft.gameArgs || "";
      if (waitField) waitField.value = String(scriptDraft.gameWaitSeconds ?? 30);
      if (launchButton) {
        const pressed = scriptDraft.launchGame === true;
        launchButton.setAttribute("aria-pressed", pressed ? "true" : "false");
        launchButton.dataset.state = pressed ? "on" : "off";
        const stateText = launchButton.querySelector("[data-switch-state]");
        if (stateText) stateText.textContent = pressed ? t("common.enabled") : t("common.disabled");
      }
    }
    scriptDraft._lastGameMode = currentMode;
  }
  // self-managed-pc-launch：PC 模式下启动参数与等待秒数禁用（保留显示值）；
  // 游戏路径保留可填写，用于任务失败时的强制关闭游戏；「启动游戏」开关先关闭再禁用。
  // 禁用元件不再派发指针/聚焦事件，禁用提示气泡挂在外层容器上，覆盖字段原有帮助气泡。
  const lockedHelp = t("scripts.editor.pc_client_disabled");
  const exeField = $dom("#sm-game-exe");
  if (argsField) argsField.disabled = lockPcFields;
  if (waitField) waitField.disabled = lockPcFields;
  if (exeField) exeField.disabled = false;
  setFieldBubble(argsField, lockPcFields ? lockedHelp : (isEmu ? t("scripts.android.arguments_mode_help") : ""));
  setFieldBubble(waitField, lockPcFields ? lockedHelp : t("scripts.editor.game_launch.wait_help"));
  const launch = launchButton;
  if (launch) {
    if (lockPcFields) {
      // 先关闭（状态同步与 toggleSmFlag 一致），再禁用
      launch.setAttribute("aria-pressed", "false");
      launch.dataset.state = "off";
      const stateText = launch.querySelector("[data-switch-state]");
      if (stateText) stateText.textContent = t("common.disabled");
    }
    launch.disabled = lockPcFields;
    launch.setAttribute("aria-disabled", lockPcFields ? "true" : "false");
    const launchRow = launch.closest(".switch-row");
    if (launchRow) {
      if (lockPcFields) launchRow.dataset.tooltip = lockedHelp;
      else delete launchRow.dataset.tooltip;
    }
  }
  const hint = $dom("#sm-self-managed-hint");
  if (hint) hint.hidden = !lockPcFields;
}

/** 设置字段容器（.field）的帮助气泡：文本为空时移除；disabled 字段的气泡由容器承载。 */
function setFieldBubble(field, text) {
  const container = field?.closest(".field");
  if (!container) return;
  if (text) container.dataset.help = text;
  else delete container.dataset.help;
}

function scriptCardMarkup(script) {
  const pluginStatus = scriptPluginStatus(script, state.plugins || []);
  const unavailable = pluginStatus.specialized && !pluginStatus.available;
  const unavailableMessage = unavailable ? scriptPluginUnavailableMessage(script, state.plugins || []) : "";
  const pluginBadge = !pluginStatus.specialized
    ? `<span class="badge muted">${t("scripts.general_script")}</span>`
    : pluginStatus.missing
      ? `<span class="badge bad" data-testid="script-plugin-badge" title="${esc(unavailableMessage)}">${t("common.plugin.unknown")}</span>`
      : `<span class="badge ${unavailable ? "warn" : "muted"}" data-testid="script-plugin-badge"${unavailable ? ` title="${esc(unavailableMessage)}"` : ""}>${t("scripts.label.specialized", { name: esc(pluginStatus.displayName) })}</span>`;
  const judgeBadge = script.judgeScriptEnabled === true && String(script.judgeScript || "").trim()
    ? `<span class="badge muted" data-testid="script-judge-badge">${t("scripts.judge_script")}</span>`
    : String(script.successKeywords || "").trim() || String(script.failureKeywords || "").trim()
      ? `<span class="badge muted" data-testid="script-judge-badge">${t("scripts.keyword_judge")}</span>`
      : "";
  const gameModeBadge = script.launchGame === true
    ? `<span class="badge muted" data-testid="script-game-mode-badge">${t(String(script.gameMode || "").trim().toLowerCase() === "emulator" ? "scripts.android_emulator" : "scripts.pc_client")}</span>`
    : "";
  const longBadge = script.logStallTimeoutMinutes === -1
    ? `<span class="badge warn" data-testid="script-long-badge">${t("scripts.long_running_policy")}</span>`
    : "";
  const entityState = unavailable
    ? ` class="entity-link is-unavailable" disabled aria-disabled="true" title="${esc(unavailableMessage)}"`
    : ' class="entity-link"';
  return `<article class="script-card${unavailable ? " is-unavailable" : ""}" data-testid="script-card" data-dnd-id="${esc(script.id)}">
    <span class="drag-handle" role="button" tabindex="0" aria-label="${esc(t("common.reorder.keyboard_help"))}" title="${esc(t("common.drag_to_reorder"))}">${icon("grip")}</span>
    <img class="script-ico" src="${esc(scriptFallbackIcon)}" alt="" width="36" height="36" loading="lazy" data-icon-id="${esc(script.id)}">
    <div class="script-main">
      <button${entityState} type="button" data-action="edit-script" data-id="${esc(script.id)}" aria-label="${esc(t("scripts.accessibility.instance_action", { action: unavailable ? t("common.error.specialized_script_instance") : t("scripts.edit_script_instance"), name: script.name }))}"><span class="scroll-text"><span class="scroll-inner">${esc(script.name)}</span></span></button>
    <div class="meta-line script-meta">${pluginBadge}${gameModeBadge}${judgeBadge}${longBadge}${pluginSlotMarkup("scripts.list.badges", `script-${script.id}`, "script-plugin-slot", { mode: "list", primaryId: script.id })}</div>
    </div>
    <div class="script-ops row-actions entity-actions">
      <button class="tertiary" type="button" data-action="edit-script" data-id="${esc(script.id)}"${unavailable ? ` title="${esc(unavailableMessage)}"` : ""}>${t("scripts.edit_script")}</button>
      <button class="danger" type="button" data-action="delete-script" data-id="${esc(script.id)}" data-name="${esc(script.name)}">${t("scripts.delete_script")}</button>
    </div>
  </article>`;
}

export async function pageScripts(token) {
  if (!isCurrent("scripts", token)) return;
  navActive("scripts");
  setTopbarTitle(t("common.script_instance"));
  let scripts, status;
  try {
    [scripts, status] = await Promise.all([api("GET", "/api/scripts"), api("GET", "/api/status")]);
  } catch (error) {
    if (isCurrent("scripts", token)) render(`<div class="empty"><strong>${t("scripts.load.instances_failed")}</strong>${esc(error.message)}</div>`);
    return;
  }
  if (!isCurrent("scripts", token)) return;
  state.scripts = scripts;
  state.plugins = status.plugins || [];
  const atLimit = !!(state.limits && scripts.length >= state.limits.maxScripts);
  const action = `<button class="primary" type="button" data-action="open-script-modal" data-testid="new-script" ${atLimit ? "disabled" : ""}>${t("scripts.new_script_instance")}${atLimit ? ` (${scripts.length}/${state.limits.maxScripts})` : ""}</button>`;
  const totalPages = Math.max(1, Math.ceil(scripts.length / SCRIPT_PAGE_SIZE));
  if (scriptPage > totalPages) scriptPage = totalPages;
  const pageItems = scripts.slice((scriptPage - 1) * SCRIPT_PAGE_SIZE, scriptPage * SCRIPT_PAGE_SIZE);
  const content = scripts.length === 0
    ? `<div class="empty"><strong>${t("scripts.no_script_instances_yet")}</strong><span>${t("scripts.page.empty_help")}</span><a class="back-link" href="#/scripts" data-action="open-script-modal">${t("scripts.new_script_instance")}</a></div>`
    : `<section class="card list-surface"><div class="script-grid">
      ${pageItems.map(script => scriptCardMarkup(script)).join("")}
    </div>${pagerMarkup("scripts", scriptPage, SCRIPT_PAGE_SIZE, scripts.length)}</section>`;
  render(pageHeader(t("scripts.automation_management"), t("common.script_instance"), t("scripts.page.help"), action) + content);
  await renderPluginSlots(document.querySelector("#view"));
  registerPager("scripts", page => { scriptPage = page; pageScripts(state.routeToken); });
  wireScriptIcons();
  hydrateIcons($dom("#view"));
  wireScriptDnd();
}

/** 拖拽排序：只改变当前分页区间，其他分页保持原位置与相对顺序。 */
function wireScriptDnd() {
  const list = $dom(".script-grid");
  if (!list) return;
  initDndList(list, { onDrop: (ids) => reorderScripts(ids) });
}

/** 把当前页新顺序写回全量列表，提交 PUT /api/scripts/order。 */
async function reorderScripts(visibleIds) {
  const full = replacePageOrder(state.scripts, scriptPage, SCRIPT_PAGE_SIZE, visibleIds);
  try {
    await api("PUT", "/api/scripts/order", { ids: full.map(item => item.id) });
    toast(t("scripts.script_order_saved"));
    await pageScripts(state.routeToken);
  } catch (error) {
    toast(error.message, "error");
    await pageScripts(state.routeToken);
  }
}

function wireScriptIcons() {
  $dom("#view")?.querySelectorAll(".script-ico").forEach(img => {
    img.addEventListener("error", () => {
      if (img.dataset.fallback && !img.src.startsWith("data:")) img.src = img.dataset.fallback;
    }, { once: true });
  });
}

/** 新建入口：无专用插件时直接打开默认配置弹窗；有专用插件时先弹出选择卡片层。 */
export function openNewScriptChooser() {
  const specials = specializedPlugins();
  if (specials.length === 0) {
    openScriptModal();
    return;
  }
  const body = `<div class="new-script-chooser">
    <button type="button" class="chooser-card" data-action="open-script-type" data-plugin="">
      <strong>${t("scripts.new_general_script_instance")}</strong><span class="muted">${t("scripts.editor.manual_config_help")}</span>
    </button>
    ${specials.map(p => `<button type="button" class="chooser-card" data-action="open-script-type" data-plugin="${esc(p.name)}">
      <strong class="scroll-text"><span class="scroll-inner">${t("scripts.action.create_specialized", { plugin: esc(p.displayName) })}</span></strong><span class="muted">${t("scripts.plugin.config_auto")}</span>
    </button>`).join("")}
  </div>`;
  const footer = `<button class="ghost" type="button" data-action="close-modal">${t("common.cancel")}</button>`;
  showModal(modalShell(t("scripts.new_script_instance"), body, footer), false, true, true);
}

export function editScript(id) {
  const script = (state.scripts || []).find(item => item.id === id);
  const unavailableMessage = script ? scriptPluginUnavailableMessage(script, state.plugins || []) : "";
  if (unavailableMessage) {
    toast(unavailableMessage, "error");
    return;
  }
  return openScriptModal(id);
}

export async function openScriptModal(id = "", plugin = "") {
  let script = id ? state.scripts.find(item => item.id === id) : null;
  if (id && !script) {
    try {
      state.scripts = await api("GET", "/api/scripts");
      script = state.scripts.find(item => item.id === id);
    } catch (error) {
      toast(t("scripts.load.failed", { reason: error.message }), "error");
      return;
    }
  }
  if (script) {
    const unavailableMessage = scriptPluginUnavailableMessage(script, state.plugins || []);
    if (unavailableMessage) {
      toast(unavailableMessage, "error");
      return;
    }
  }
  const value = script || {};
  const pluginType = value.pluginType || plugin || "";
  const isSpecial = !!pluginType;
  scriptDraft = {
    id: value.id || "", pluginType, name: value.name || "", rootPath: value.rootPath || "",
    pluginInputs: value.pluginInputs && typeof value.pluginInputs === "object" ? value.pluginInputs : {},
    mainExe: value.mainExe || "", args: value.args || "", configPath: value.configPath || "", logPath: value.logPath || "",
    launchGame: !!value.launchGame, gameMode: value.gameMode === "emulator" ? "emulator" : "pc", gameExe: value.gameExe || "", gameArgs: value.gameArgs || "",
    _lastGameMode: value.gameMode === "emulator" ? "emulator" : "pc",
    // 专项脚本实例的「强制关闭」默认打开（通用脚本默认关闭）；编辑时保留用户已有设置。
    gameWaitSeconds: value.gameWaitSeconds ?? 30, forceCloseGame: isSpecial ? (value.forceCloseGame ?? true) : !!value.forceCloseGame,
    maxAttempts: value.maxAttempts ?? 3, logStallTimeoutMinutes: value.logStallTimeoutMinutes ?? 5,
    totalTimeoutMinutes: value.totalTimeoutMinutes ?? 120,
    successKeywords: value.successKeywords || "", failureKeywords: value.failureKeywords || "",
    judgeScriptEnabled: !!value.judgeScriptEnabled, judgeScriptLanguage: value.judgeScriptLanguage || "", judgeScript: value.judgeScript || "",
    autoUpdateConfig: isSpecial ? true : (value.autoUpdateConfig ?? true),
  };
  const d = scriptDraft;
  const l = state.limits || {};
  const title = isSpecial
    ? (id ? t("scripts.action.edit_specialized", { plugin: esc(pluginDisplayName(pluginType, state.plugins || [])) }) : t("scripts.action.new_specialized", { plugin: esc(pluginDisplayName(pluginType, state.plugins || [])) }))
    : (id ? t("scripts.edit_script_instance") : t("scripts.new_general_script_instance"));
  const body = isSpecial
    ? `<div class="form-grid">
      ${valueField("sm-name", `${t("scripts.script_name")} <span class='req'>*</span>`, d.name)}
      ${pathField("sm-root", `${t("scripts.script_root_directory")} <span class='req'>*</span>`, d.rootPath, "folder", `placeholder="${t("scripts.script_root_directory")}"`, "", "", t("scripts.plugin.config_auto_help"))}
    </div>
    <div class="subsection"><div class="section-heading"><h3>${t("scripts.game_integration")}</h3><span class="muted">${t("scripts.editor.path_adb_cleanup_help")}</span></div>
      <div class="toggle-grid switch-grid">
        ${switchControl("sm-launch", t("scripts.launch_game"), t("scripts.editor.game_launch.help"), d.launchGame, "toggle-sm-flag", 'data-flag="launch"')}
        ${switchControl("sm-force", t("scripts.force_close"), t("scripts.game.cleanup_help"), d.forceCloseGame, "toggle-sm-flag", 'data-flag="force"')}
        ${switchControl("sm-autoupdate", t("scripts.editor.config_auto_update"), t("scripts.editor.config_sync_help"), true, "toggle-sm-flag", 'data-flag="autoupdate" data-testid="sm-autoupdate" disabled')}
      </div>
      <div id="sm-game-box" class="nested-panel">
        ${gameBoxHtml(d, emulatorAllowed(pluginType))}
      </div>
    </div>
    <div class="subsection"><div class="section-heading"><h3>${t("scripts.run_settings")}</h3></div>
      <div class="form-grid three">
        ${valueField("sm-attempts", `${t("scripts.editor.retry.attempts_label")} <span class='req'>*</span>`, d.maxAttempts, "number", `min="${l.minAttempts ?? 1}" max="${l.maxAttempts ?? 10}"`, t("scripts.editor.retry.attempts_help"))}
        ${valueField("sm-stall", `${t("scripts.log_stall_timeout_minutes")} <span class='req'>*</span>`, d.logStallTimeoutMinutes, "number", `min="-1" max="${l.maxStallMinutes ?? 60}"`, t("scripts.editor.retry.stall_timeout_help"))}
        ${valueField("sm-total", `${t("scripts.total_timeout_minutes")} <span class='req'>*</span>`, d.totalTimeoutMinutes, "number", `min="-1" max="${l.maxTotalMinutes ?? 720}"`, t("scripts.validation.total_timeout_help"))}
      </div>
    </div>`
    : `<div class="form-grid">
        ${valueField("sm-name", `${t("scripts.script_name")} <span class='req'>*</span>`, d.name)}
      ${pathField("sm-root", `${t("scripts.script_root_directory")} <span class='req'>*</span>`, d.rootPath, "folder", `placeholder="${t("scripts.script_root_directory")}"`)}
    </div>
    <div class="form-grid">
      ${pathField("sm-exe", `${t("scripts.main_program_path")} <span class='req'>*</span>`, d.mainExe, "file", `placeholder="${t("scripts.main_program_file")}"`, t("common.executable_file_filter"), `data-path-root-target="sm-root" data-path-root-error="${t("scripts.main_program_path_error")}"`, t("scripts.editor.path_edit_help"))}
      ${valueField("sm-args", t("scripts.script_startup_arguments"), d.args, "text", `placeholder="${t("scripts.optional_startup_arguments")}"`, t("scripts.editor.startup_args.help"))}
    </div>
    <div class="form-grid">
      ${pathField("sm-config", `${t("scripts.configuration_file_folder")} <span class='req'>*</span>`, d.configPath, "file-or-folder", `placeholder="${t("scripts.editor.root_required")}"`, "", `data-path-root-target="sm-root" data-path-root-error="${t("scripts.script_root_directory_error")}"`, t("scripts.editor.config_path.help"))}
      ${pathField("sm-log", `${t("scripts.editor.log_path.help")} <span class='req'>*</span>`, d.logPath, "file-or-folder", `placeholder="${t("scripts.log_file_path")}"`, t("scripts.log_file"), `data-path-root-target="sm-root" data-path-root-error="${t("scripts.script_root_directory_error")}"`, t("scripts.editor.log_path.pattern_help"))}
    </div>
    <div class="subsection"><div class="section-heading"><h3>${t("scripts.game_integration")}</h3></div>
      <div class="toggle-grid switch-grid">
        ${switchControl("sm-launch", t("scripts.launch_game"), t("scripts.editor.game_launch.help"), d.launchGame, "toggle-sm-flag", 'data-flag="launch"')}
        ${switchControl("sm-force", t("scripts.force_close"), t("scripts.game.cleanup_help"), d.forceCloseGame, "toggle-sm-flag", 'data-flag="force"')}
        ${switchControl("sm-autoupdate", t("scripts.editor.config_auto_update"), t("scripts.editor.config_sync_help"), d.autoUpdateConfig, "toggle-sm-flag", 'data-flag="autoupdate" data-testid="sm-autoupdate"')}
      </div>
      <div id="sm-game-box" class="nested-panel">
        ${gameBoxHtml(d, emulatorAllowed(pluginType))}
      </div>
    </div>
    <div class="subsection"><div class="section-heading"><h3>${t("scripts.run_settings")}</h3></div>
      <div class="form-grid three">
        ${valueField("sm-attempts", `${t("scripts.editor.retry.attempts_label")} <span class='req'>*</span>`, d.maxAttempts, "number", `min="${l.minAttempts ?? 1}" max="${l.maxAttempts ?? 10}"`, t("scripts.editor.retry.attempts_help"))}
        ${valueField("sm-stall", `${t("scripts.log_stall_timeout_minutes")} <span class='req'>*</span>`, d.logStallTimeoutMinutes, "number", `min="-1" max="${l.maxStallMinutes ?? 60}"`, t("scripts.editor.retry.stall_timeout_help"))}
        ${valueField("sm-total", `${t("scripts.total_timeout_minutes")} <span class='req'>*</span>`, d.totalTimeoutMinutes, "number", `min="-1" max="${l.maxTotalMinutes ?? 720}"`, t("scripts.validation.total_timeout_help"))}
      </div>
      <div class="subsection judge-box"><div class="section-heading"><h3>${t("scripts.custom_completion_markers")}</h3></div>
        <div id="sm-kw-box" ${d.judgeScriptEnabled ? "hidden" : ""}>
          <div class="field" data-help="${t("scripts.success_keyword_help")}"><label class="field-label" for="sm-succ-kw">${t("scripts.success_keywords")}</label>
          <textarea id="sm-succ-kw" placeholder="${t("scripts.judge.keyword_syntax")}">${esc(d.successKeywords)}</textarea></div>
          <div class="field" data-help="${t("scripts.failure_keyword_help")}"><label class="field-label" for="sm-fail-kw">${t("scripts.failure_keywords")}</label>
          <textarea id="sm-fail-kw" placeholder="${t("scripts.judge.failure_marker")}">${esc(d.failureKeywords)}</textarea></div>
        </div>
        <div id="sm-script-box" ${d.judgeScriptEnabled ? "" : "hidden"}>
          ${selectField("sm-judge-lang", t("scripts.judge_script_language"), d.judgeScriptLanguage === "python" ? "python" : "javascript", [{ value: "javascript", label: t("scripts.javascript_built_in_engine") }, { value: "python", label: t("scripts.python_system_interpreter") }], "", t("scripts.judge.language_help"))}
           <div class="field" data-help="${t("scripts.judge_script_input_output_help")}"><label class="field-label" for="sm-judge-code">${t("scripts.judge_script")} ${t("scripts.code")}</label><textarea id="sm-judge-code" class="mono code-area" placeholder="${t("scripts.output_a_json_result")}">${esc(d.judgeScript)}</textarea></div>
        </div>
        <div class="judge-actions">
          <button class="judge-upload-button" type="button" data-action="upload-judge-script" id="sm-upload-btn" ${d.judgeScriptEnabled ? "" : "hidden"}>${t("scripts.upload_script_file")}</button>
          <button class="judge-mode-card mode-toggle" type="button" data-action="toggle-judge-mode" id="sm-mode-btn" data-help="${t("scripts.judge_script_mode_help")}" data-toggle-text="false" data-hint="${t("scripts.script_takes_priority")}" aria-label="${t("scripts.editor.judge.priority_help")}" aria-pressed="${d.judgeScriptEnabled ? "true" : "false"}">${t("scripts.use_judge_script")}<span class="judge-toggle-track" aria-hidden="true"><span class="judge-toggle-thumb"></span></span></button>
        </div>
      </div>
    </div>`;
  const footer = `<button class="ghost" type="button" data-action="close-modal">${t("common.cancel")}</button><button class="primary" type="button" data-action="save-script">${t("common.save")}</button>`;
  showModal(modalShell(title, body + pluginSlotMarkup("scripts.editor.sections", "scripts.editor.sections", "script-editor-plugin-slot", { mode: id ? "edit" : "create", primaryId: id || "" }), footer), true, true, true);
  void renderPluginSlots(document);
  syncScriptGhostState();
  changeGameMode();
  syncJudgeBox();
  const rootInput = $dom("#sm-root");
  rootInput?.addEventListener("input", syncScriptGhostState);
  rootInput?.addEventListener("change", event => {
    syncScriptGhostState();
    if (isSpecial && event.target.value.trim()) probeSpecialRoot(event.target.value.trim(), pluginType);
  });
  rootInput?.addEventListener("keyup", syncScriptGhostState);
}

async function probeSpecialRoot(rootPath, pluginType) {
  try {
    await api("POST", "/api/scripts/probe", { rootPath, pluginType, inputs: {} });
  } catch (error) {
    toast(t("scripts.plugin.config_derive_failed", { reason: error.message }), "error");
  }
}

/** 收集编辑弹窗携带的既有输入值：用户级接管配置（configInputs）落地后，实例表单不再渲染输入字段，
 *  编辑既有实例时原样回传持久化值，避免整表替换丢失。 */
function collectPluginInputs() {
  return scriptDraft.pluginInputs && typeof scriptDraft.pluginInputs === "object" ? { ...scriptDraft.pluginInputs } : {};
}

export function syncScriptGhostState() {
  const root = $dom("#sm-root");
  const hasRoot = !!(root && root.value.trim());
  ["sm-exe", "sm-args", "sm-config", "sm-log"].forEach(id => {
    const element = $dom("#" + id);
    if (element) {
      element.disabled = !hasRoot;
      element.closest(".nxp-path")?.querySelectorAll("[data-path-trigger]").forEach(trigger => {
        trigger.disabled = !hasRoot;
      });
    }
  });
}

/** 自定义完成标志开关（按钮切换模式）：开启显示脚本区（隐藏关键字区），关闭反之。 */
export function syncJudgeBox() {
  const enabled = $dom("#sm-mode-btn")?.getAttribute("aria-pressed") === "true";
  const kw = $dom("#sm-kw-box");
  const script = $dom("#sm-script-box");
  const upload = $dom("#sm-upload-btn");
  if (kw) kw.hidden = enabled;
  if (script) script.hidden = !enabled;
  if (upload) upload.hidden = !enabled;
}

/** 切换「使用判断脚本」按钮状态。 */
export function toggleJudgeMode() {
  const btn = $dom("#sm-mode-btn");
  if (!btn) return;
  btn.setAttribute("aria-pressed", btn.getAttribute("aria-pressed") === "true" ? "false" : "true");
  syncJudgeBox();
}

/** 切换游戏联动开关按钮状态（启动游戏｜强制关闭｜自动更新配置）。 */
function toggleSmFlag(flag) {
  const btn = $dom("#sm-" + flag);
  if (!btn) return;
  const pressed = btn.getAttribute("aria-pressed") !== "true";
  btn.setAttribute("aria-pressed", pressed ? "true" : "false");
  btn.dataset.state = pressed ? "on" : "off";
  const stateText = btn.querySelector("[data-switch-state]");
  if (stateText) stateText.textContent = pressed ? t("common.enabled") : t("common.disabled");
}

/** 上传判断脚本文件：读取内容填入代码框，按扩展名自动识别语言（.py=Python，其余=JavaScript）。 */
export function uploadJudgeScript() {
  const input = document.createElement("input");
  input.type = "file";
  input.accept = ".js,.py";
  return new Promise(resolve => {
    let settled = false;
    let focusTimer = null;
    const finish = () => {
      if (settled) return;
      settled = true;
      if (focusTimer) window.clearTimeout(focusTimer);
      window.removeEventListener("focus", onWindowFocus);
      resolve();
    };
    const onWindowFocus = () => {
      if (!input.files?.length) focusTimer = window.setTimeout(finish, 0);
    };
    window.addEventListener("focus", onWindowFocus);
    input.addEventListener("cancel", finish, { once: true });
    input.addEventListener("change", () => {
    const file = input.files?.[0];
    if (!file) { finish(); return; }
    if (file.size > 256 * 1024) {
      toast(t("scripts.validation.file_size"), "error");
      finish();
      return;
    }
    const reader = new FileReader();
    reader.onload = () => {
      const lang = file.name.toLowerCase().endsWith(".py") ? "python" : "javascript";
      const code = $dom("#sm-judge-code");
      const language = $dom("#sm-judge-lang");
      if (code) code.value = String(reader.result || "");
      if (language) {
        language.value = lang;
        language.dispatchEvent(new Event("change", { bubbles: true }));
      }
      toast(t("scripts.status.loaded", { language: lang === "python" ? "Python" : "JavaScript" }));
      finish();
    };
    reader.onerror = () => {
      toast(t("scripts.file.read_failed"), "error");
      finish();
    };
    reader.readAsText(file, "utf-8");
    });
    input.click();
  });
}

/** 去除成对首尾引号（"…" / '…'），与后端 StripPathQuotes 语义一致；内部引号保留。</summary> */
function stripQuotes(value) {
  const trimmed = (value || "").trim();
  if (trimmed.length >= 2) {
    const first = trimmed[0];
    const last = trimmed[trimmed.length - 1];
    if ((first === '"' && last === '"') || (first === "'" && last === "'")) {
      return trimmed.slice(1, -1).trim();
    }
  }
  return trimmed;
}

export async function saveScript() {
  if (scriptDraft?.id) {
    const existing = (state.scripts || []).find(item => item.id === scriptDraft.id);
    const unavailableMessage = existing
      ? scriptPluginUnavailableMessage(existing, state.plugins || [])
      : "";
    if (unavailableMessage) {
      toast(unavailableMessage, "error");
      return;
    }
  }
  const isSpecial = !!scriptDraft.pluginType;
  const required = isSpecial
    ? [["sm-name", t("scripts.script_name")], ["sm-root", t("scripts.script_root_directory")], ["sm-attempts", t("scripts.maximum_attempts")], ["sm-stall", t("scripts.log_inactivity_limit_minutes")], ["sm-total", t("scripts.total_run_time_limit_minutes")]]
    : [["sm-name", t("scripts.script_name")], ["sm-root", t("scripts.script_root_directory")], ["sm-exe", t("scripts.main_program_path")], ["sm-config", t("scripts.configuration_file_path")], ["sm-log", t("scripts.log_path")], ["sm-attempts", t("scripts.maximum_attempts")], ["sm-stall", t("scripts.log_inactivity_limit_minutes")], ["sm-total", t("scripts.total_run_time_limit_minutes")]];
  let firstError = null;
  for (const [id, label] of required) {
    const element = $dom("#" + id);
    if (!element?.value.trim()) {
      setRequiredFieldError(id);
      firstError ??= id;
      continue;
    }
    clearFieldError(id);
  }
  if (firstError) { toast(t("scripts.validation.required_fields"), "error"); return; }
  const l = state.limits || {};
  const ILLEGAL_PATH = /["<>|?*{}]/;
  const ILLEGAL_LOG = /["<>|?]/;
  const pathFields = isSpecial
    ? [["sm-root", t("scripts.script_root_directory"), ILLEGAL_PATH]]
    : [["sm-root", t("scripts.script_root_directory"), ILLEGAL_PATH], ["sm-exe", t("scripts.main_program_path"), ILLEGAL_PATH], ["sm-config", t("scripts.configuration_file_folder"), ILLEGAL_PATH], ["sm-log", t("scripts.editor.log_path.help"), ILLEGAL_LOG]];
  for (const [id, label, illegal] of pathFields) {
    const value = stripQuotes($dom("#" + id)?.value);
    if (illegal.test(value)) {
      setFieldError(id, t("scripts.validation.invalid_characters", { label }));
      toast(t("scripts.validation.invalid_characters", { label }), "error");
      return;
    }
  }
  const nameBytes = new TextEncoder().encode($dom("#sm-name").value.trim()).length;
  if (nameBytes > MAX_ENTITY_NAME_BYTES) {
    setFieldError("sm-name", t("scripts.validation.name_length", { bytes: MAX_ENTITY_NAME_BYTES }));
    toast(t("scripts.validation.name_length", { bytes: MAX_ENTITY_NAME_BYTES }), "error");
    return;
  }
  const name = $dom("#sm-name").value.trim();
  if (hasEntityNameConflict(state.scripts, name, scriptDraft.id)) {
    setFieldInvalid("sm-name");
    toast(t("scripts.validation.name_duplicate"), "error");
    return;
  }
  const attempts = parseInt($dom("#sm-attempts")?.value, 10);
  const stall = parseInt($dom("#sm-stall")?.value, 10);
  const total = parseInt($dom("#sm-total")?.value, 10);
  if (!(attempts >= (l.minAttempts ?? 1)) || !(attempts <= (l.maxAttempts ?? 10))) {
    setFieldError("sm-attempts", t("scripts.validation.attempts_range", { min: l.minAttempts ?? 1, max: l.maxAttempts ?? 10 }));
    toast(t("scripts.validation.attempts_range", { min: l.minAttempts ?? 1, max: l.maxAttempts ?? 10 }), "error");
    return;
  }
  // 日志无更新上限为 -1 定义长时脚本；普通脚本不能禁用运行总时间上限
  const longStall = stall === -1;
  const unlimitedTotal = total === -1;
  if (!longStall && unlimitedTotal) {
    setFieldError("sm-total", t("scripts.validation.timeout_dependency"));
    toast(t("scripts.validation.timeout_dependency"), "error");
    return;
  }
  if (!longStall && (!(stall >= (l.minStallMinutes ?? 1)) || !(stall <= (l.maxStallMinutes ?? 60)))) {
    setFieldError("sm-stall", t("scripts.validation.log_timeout_range", { min: l.minStallMinutes ?? 1, max: l.maxStallMinutes ?? 60 }));
    toast(t("scripts.validation.log_timeout_range", { min: l.minStallMinutes ?? 1, max: l.maxStallMinutes ?? 60 }), "error");
    return;
  }
  if (!unlimitedTotal && (!(total >= (l.minTotalMinutes ?? 5)) || !(total <= (l.maxTotalMinutes ?? 720)))) {
    setFieldError("sm-total", t("scripts.validation.total_timeout_range", { min: l.minTotalMinutes ?? 5, max: l.maxTotalMinutes ?? 720 }));
    toast(t("scripts.validation.total_timeout_range", { min: l.minTotalMinutes ?? 5, max: l.maxTotalMinutes ?? 720 }), "error");
    return;
  }
  const judgeEnabled = ($dom("#sm-mode-btn")?.getAttribute("aria-pressed") ?? "false") === "true";
  const judgeCode = $dom("#sm-judge-code")?.value ?? "";
  if (judgeEnabled && !judgeCode.trim()) {
    setRequiredFieldError("sm-judge-code");
    toast(t("scripts.editor.judge_code_help"), "error");
    return;
  }
  const launchGame = $dom("#sm-launch")?.getAttribute("aria-pressed") === "true";
  const gameMode = $dom("#sm-mode")?.value === "emulator" ? "emulator" : "pc";
  const selfManagedPc = selfManagedPcLaunch(scriptDraft.pluginType) && gameMode !== "emulator";
  const gameArgs = $dom("#sm-game-args")?.value.trim() || "";
  const gameWaitSeconds = +($dom("#sm-game-wait")?.value || 0) || 0;
  const gameExe = stripQuotes($dom("#sm-game-exe")?.value);
  if (!gameExe) {
    setRequiredFieldError("sm-game-exe");
    toast(t(gameMode === "emulator" ? "scripts.validation.adb_required" : "scripts.validation.game_path_required"), "error");
    return;
  }
  if (gameMode === "emulator") {
    const colon = gameExe.lastIndexOf(":");
    const port = parseInt(gameExe.slice(colon + 1), 10);
    if (colon <= 0 || !(port >= 1 && port <= 65535)) {
      setFieldError("sm-game-exe", t("scripts.validation.adb_address"));
      toast(t("scripts.validation.adb_address"), "error");
      return;
    }
  } else if (ILLEGAL_PATH.test(gameExe)) {
    setFieldError("sm-game-exe", t("scripts.validation.game_path"));
    toast(t("scripts.validation.game_path"), "error");
    return;
  }
  const payload = {
    id: scriptDraft.id, pluginType: scriptDraft.pluginType || "", name, rootPath: stripQuotes($dom("#sm-root")?.value),
    pluginInputs: isSpecial ? collectPluginInputs() : {},
    mainExe: isSpecial ? "" : stripQuotes($dom("#sm-exe")?.value), args: isSpecial ? "" : $dom("#sm-args").value.trim(),
    configPath: isSpecial ? "" : stripQuotes($dom("#sm-config")?.value), logPath: isSpecial ? "" : stripQuotes($dom("#sm-log")?.value),
    // self-managed PC 只禁用宿主本次启动行为，持久化仍保留 dormant 草稿，切回模拟器可继续使用。
    launchGame: selfManagedPc ? scriptDraft.launchGame === true : launchGame,
    gameMode,
    gameExe,
    gameArgs: selfManagedPc ? (scriptDraft.gameArgs || "") : gameArgs,
    gameWaitSeconds: selfManagedPc ? (scriptDraft.gameWaitSeconds ?? 30) : gameWaitSeconds,
    forceCloseGame: $dom("#sm-force")?.getAttribute("aria-pressed") === "true", maxAttempts: attempts, logStallTimeoutMinutes: stall, totalTimeoutMinutes: total,
    successKeywords: isSpecial ? "" : ($dom("#sm-succ-kw")?.value ?? ""), failureKeywords: isSpecial ? "" : ($dom("#sm-fail-kw")?.value ?? ""),
    judgeScriptEnabled: judgeEnabled, judgeScriptLanguage: $dom("#sm-judge-lang")?.value || "", judgeScript: judgeCode,
    autoUpdateConfig: isSpecial ? true : ($dom("#sm-autoupdate")?.getAttribute("aria-pressed") === "true"),
  };
  try {
    let saved;
    if (payload.id) saved = await api("PUT", "/api/scripts/" + payload.id, payload);
    else saved = await api("POST", "/api/scripts", payload);
    closeModal();
      toast(t("scripts.script_instance_saved"));
    applySaveValidation(saved?.validation);
    const token = state.routeToken;
    await pageScripts(token);
  } catch (error) {
    if (error?.code === "duplicate_name") {
      setFieldInvalid("sm-name");
      toast(t("scripts.validation.name_duplicate"), "error");
      return;
    }
    toast(error.message, "error");
  }
}

/** 保存脚本实例响应中的专项校验结果：角落通知提醒配置差异（通知为提醒性质，配置不被修改）。 */
function applySaveValidation(validation) {
  if (!validation) return;
  if (validation.error) {
    toast(t("scripts.error.plugin_validation"), "error");
  }
  for (const item of validation.notifications || []) {
    pushNotice(item.title || "", item.body || "", item.kind || "info");
  }
  for (const item of validation.toasts || []) {
    toast(item.message || "", item.kind || "info");
  }
}

export function deleteScript(id, name) {
  confirmModal(t("scripts.delete_script_instance"), t("scripts.confirm_delete_script_instance", { name: esc(name) }), "confirm-delete-script", { id, name });
}

export async function confirmDeleteScript(id, name) {
  try { await api("DELETE", "/api/scripts/" + id); closeModal(); toast(t("scripts.script_instance_deleted")); await pageScripts(state.routeToken); }
  catch (error) { toast(error.message, "error"); }
}

export const actions = {
  "open-script-modal": () => openNewScriptChooser(),
  "open-script-type": target => openScriptModal("", target.dataset.plugin || ""),
  "edit-script": target => editScript(target.dataset.id),
  "delete-script": target => deleteScript(target.dataset.id, target.dataset.name),
  "confirm-delete-script": target => withBusy(target, () => confirmDeleteScript(target.dataset.id, target.dataset.name)),
  "save-script": target => withBusy(target, () => saveScript()),
  "change-sm-mode": () => changeGameMode(),
  "upload-judge-script": target => withBusy(target, () => uploadJudgeScript()),
  "toggle-judge-mode": () => toggleJudgeMode(),
  "toggle-sm-flag": target => toggleSmFlag(target.dataset.flag),
};
