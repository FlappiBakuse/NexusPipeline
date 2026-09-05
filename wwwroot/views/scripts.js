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
    ? `<div class="form-grid game-mode-row">${selectField("sm-mode", "启动方式", isEmu ? "emulator" : "pc", [{ value: "pc", label: "PC 客户端" }, { value: "emulator", label: "安卓模拟器" }], 'data-action="change-sm-mode"', "选择游戏启动方式；模拟器模式使用 ADB 地址。")}<div class="game-wait-field">${valueField("sm-game-wait", "启动后等待秒数", d.gameWaitSeconds, "number", 'min="0"', "启动游戏后等待指定秒数，再运行脚本。")}</div></div>`
    : `<div class="form-grid game-mode-row">${valueField("sm-game-wait", "启动后等待秒数", d.gameWaitSeconds, "number", 'min="0"', "启动游戏后等待指定秒数，再运行脚本。")}<div class="game-wait-field" aria-hidden="true"></div></div>`;
  const exeField = pathField(
    "sm-game-exe",
    isEmu ? "模拟器ADB地址 <span class='req'>*</span>" : "游戏路径 <span class='req'>*</span>",
    d.gameExe,
    "file",
    isEmu ? 'placeholder="例如 127.0.0.1:16384"' : 'placeholder="游戏可执行文件路径"',
    "可执行文件|*.exe;*.bat;*.cmd;*.com|所有文件|*.*",
    isEmu ? 'hidden aria-hidden="true"' : "",
    isEmu ? "模拟器 ADB 地址用于失败清理与重试恢复。" : "游戏路径用于失败清理与重试恢复。",
  );
  const argsField = isEmu
    ? valueField("sm-game-args", "启动参数", d.gameArgs, "text", 'placeholder="am start 参数"', "模拟器模式下，该内容会作为 adb shell am start 参数传递。")
    : valueField("sm-game-args", "启动参数", d.gameArgs);
  const selfManagedHint = !isEmu && selfManagedPcLaunch(d.pluginType)
    ? '<p id="sm-self-managed-hint" class="muted">PC 客户端由脚本自身启动；启动参数与等待秒数不可配置，游戏路径用于失败时强制关闭游戏。</p>'
    : '<p id="sm-self-managed-hint" class="muted" hidden>PC 客户端由脚本自身启动；启动参数与等待秒数不可配置，游戏路径用于失败时强制关闭游戏。</p>';
  return `${selfManagedHint}<div class="form-grid">${exeField}${argsField}</div>${modeRow}`;
}

/** 启动方式切换：更新游戏路径/ADB 地址字段的标签与提示；self-managed-pc-launch 插件的 PC 模式禁用游戏启动项。 */
export function changeGameMode() {
  const isEmu = $dom("#sm-mode")?.value === "emulator";
  const exe = $dom("#sm-game-exe");
  const args = $dom("#sm-game-args");
  const exeLabel = $dom('label[for="sm-game-exe"]');
  if (exeLabel) exeLabel.innerHTML = isEmu ? "模拟器ADB地址 <span class='req'>*</span>" : "游戏路径 <span class='req'>*</span>";
  if (exe) exe.placeholder = isEmu ? "例如 127.0.0.1:16384" : "请填写游戏可执行文件路径";
  if (args) args.placeholder = isEmu ? "am start 参数，如 -n 包名/.MainActivity" : "";
  const pathTrigger = exe?.closest(".nxp-path")?.querySelector("[data-path-trigger]");
  if (pathTrigger) {
    pathTrigger.hidden = isEmu;
    pathTrigger.setAttribute("aria-hidden", isEmu ? "true" : "false");
    pathTrigger.disabled = isEmu;
    pathTrigger.dataset.pathTitle = isEmu ? "模拟器ADB地址" : "游戏路径";
  }
  const lockPcFields = selfManagedPcLaunch(scriptDraft?.pluginType || "") && !isEmu;
  // self-managed-pc-launch：PC 模式下启动参数与等待秒数禁用（保留显示值）；
  // 游戏路径保留可填写，用于任务失败时的强制关闭游戏；「启动游戏」开关先关闭再禁用。
  // 禁用元件不再派发指针/聚焦事件，禁用提示气泡挂在外层容器上，覆盖字段原有帮助气泡。
  const lockedHelp = "使用 PC 客户端时，禁用该选项。";
  const argsField = $dom("#sm-game-args");
  const waitField = $dom("#sm-game-wait");
  const exeField = $dom("#sm-game-exe");
  if (argsField) argsField.disabled = lockPcFields;
  if (waitField) waitField.disabled = lockPcFields;
  if (exeField) exeField.disabled = false;
  setFieldBubble(argsField, lockPcFields ? lockedHelp : (isEmu ? "模拟器模式下，该内容会作为 adb shell am start 参数传递。" : ""));
  setFieldBubble(waitField, lockPcFields ? lockedHelp : "启动游戏后等待指定秒数，再运行脚本。");
  const launch = $dom("#sm-launch");
  if (launch) {
    if (lockPcFields) {
      // 先关闭（状态同步与 toggleSmFlag 一致），再禁用
      launch.setAttribute("aria-pressed", "false");
      launch.dataset.state = "off";
      const stateText = launch.querySelector("[data-switch-state]");
      if (stateText) stateText.textContent = "已停用";
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
    ? '<span class="badge muted">通用脚本</span>'
    : pluginStatus.missing
      ? `<span class="badge bad" data-testid="script-plugin-badge" title="${esc(unavailableMessage)}">未知专项</span>`
      : `<span class="badge ${unavailable ? "warn" : "muted"}" data-testid="script-plugin-badge"${unavailable ? ` title="${esc(unavailableMessage)}"` : ""}>${esc(pluginStatus.displayName)}专项</span>`;
  const judgeBadge = script.judgeScriptEnabled === true && String(script.judgeScript || "").trim()
    ? '<span class="badge muted" data-testid="script-judge-badge">判断脚本</span>'
    : String(script.successKeywords || "").trim() || String(script.failureKeywords || "").trim()
      ? '<span class="badge muted" data-testid="script-judge-badge">关键字判断</span>'
      : "";
  const gameModeBadge = script.launchGame === true
    ? `<span class="badge muted" data-testid="script-game-mode-badge">${String(script.gameMode || "").trim().toLowerCase() === "emulator" ? "安卓模拟器" : "PC 客户端"}</span>`
    : "";
  const longBadge = script.logStallTimeoutMinutes === -1
    ? '<span class="badge warn" data-testid="script-long-badge">长时策略</span>'
    : "";
  const entityState = unavailable
    ? ` class="entity-link is-unavailable" disabled aria-disabled="true" title="${esc(unavailableMessage)}"`
    : ' class="entity-link"';
  return `<article class="script-card${unavailable ? " is-unavailable" : ""}" data-testid="script-card" data-dnd-id="${esc(script.id)}">
    <span class="drag-handle" role="button" tabindex="0" aria-label="拖拽排序（方向键调整顺序）" title="拖拽排序">${icon("grip")}</span>
    <img class="script-ico" src="${esc(scriptFallbackIcon)}" alt="" width="36" height="36" loading="lazy" data-icon-id="${esc(script.id)}">
    <div class="script-main">
      <button${entityState} type="button" data-action="edit-script" data-id="${esc(script.id)}" aria-label="${unavailable ? "无法识别的专项脚本实例" : "编辑脚本实例"}：${esc(script.name)}"><span class="scroll-text"><span class="scroll-inner">${esc(script.name)}</span></span></button>
    <div class="meta-line script-meta">${pluginBadge}${gameModeBadge}${judgeBadge}${longBadge}${pluginSlotMarkup("scripts.list.badges", `script-${script.id}`, "script-plugin-slot", { mode: "list", primaryId: script.id })}</div>
    </div>
    <div class="script-ops row-actions entity-actions">
      <button class="tertiary" type="button" data-action="edit-script" data-id="${esc(script.id)}"${unavailable ? ` title="${esc(unavailableMessage)}"` : ""}>编辑脚本</button>
      <button class="danger" type="button" data-action="delete-script" data-id="${esc(script.id)}" data-name="${esc(script.name)}">删除脚本</button>
    </div>
  </article>`;
}

export async function pageScripts(token) {
  if (!isCurrent("scripts", token)) return;
  navActive("scripts");
  setTopbarTitle("脚本实例");
  let scripts, status;
  try {
    [scripts, status] = await Promise.all([api("GET", "/api/scripts"), api("GET", "/api/status")]);
  } catch (error) {
    if (isCurrent("scripts", token)) render(`<div class="empty"><strong>加载脚本实例失败</strong>${esc(error.message)}</div>`);
    return;
  }
  if (!isCurrent("scripts", token)) return;
  state.scripts = scripts;
  state.plugins = status.plugins || [];
  const atLimit = !!(state.limits && scripts.length >= state.limits.maxScripts);
  const action = `<button class="primary" type="button" data-action="open-script-modal" data-testid="new-script" ${atLimit ? "disabled" : ""}>新建脚本实例${atLimit ? `（${scripts.length}/${state.limits.maxScripts}）` : ""}</button>`;
  const totalPages = Math.max(1, Math.ceil(scripts.length / SCRIPT_PAGE_SIZE));
  if (scriptPage > totalPages) scriptPage = totalPages;
  const pageItems = scripts.slice((scriptPage - 1) * SCRIPT_PAGE_SIZE, scriptPage * SCRIPT_PAGE_SIZE);
  const content = scripts.length === 0
    ? '<div class="empty"><strong>暂无脚本实例</strong><span>创建一个脚本实例后，它会出现在这里并可加入调度队列。</span><a class="back-link" href="#/scripts" data-action="open-script-modal">新建脚本实例</a></div>'
    : `<section class="card list-surface"><div class="script-grid">
      ${pageItems.map(script => scriptCardMarkup(script)).join("")}
    </div>${pagerMarkup("scripts", scriptPage, SCRIPT_PAGE_SIZE, scripts.length)}</section>`;
  render(pageHeader("自动化管理", "脚本实例", "管理脚本入口、用户配置和运行策略。", action) + content);
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
    toast("脚本顺序已保存");
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
      <strong>新建通用脚本实例</strong><span class="muted">手动配置主程序、自启动参数、配置与日志路径</span>
    </button>
    ${specials.map(p => `<button type="button" class="chooser-card" data-action="open-script-type" data-plugin="${esc(p.name)}">
      <strong class="scroll-text"><span class="scroll-inner">新建${esc(p.displayName)}专项脚本实例</span></strong><span class="muted">由专用插件自动适配配置</span>
    </button>`).join("")}
  </div>`;
  const footer = '<button class="ghost" type="button" data-action="close-modal">取消</button>';
  showModal(modalShell("新建脚本实例", body, footer), false, true, true);
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
      toast("加载脚本失败：" + error.message, "error");
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
    ? (id ? `编辑${esc(pluginDisplayName(pluginType, state.plugins || []))}专项脚本实例` : `新建${esc(pluginDisplayName(pluginType, state.plugins || []))}专项脚本实例`)
    : (id ? "编辑脚本实例" : "新建脚本实例");
  const body = isSpecial
    ? `<div class="form-grid">
      ${valueField("sm-name", "脚本名称 <span class='req'>*</span>", d.name)}
      ${pathField("sm-root", "脚本根目录 <span class='req'>*</span>", d.rootPath, "folder", 'placeholder="脚本根目录"', "", "", `由专用插件「${pluginDisplayName(pluginType, state.plugins || [])}」自动适配脚本主程序、自启动参数、配置文件与日志路径。接管哪个配置文件/实例目录由各用户在「编辑配置」时选择。`)}
    </div>
    <div class="subsection"><div class="section-heading"><h3>游戏联动设置</h3><span class="muted">路径/ADB 用于失败清理与重试恢复</span></div>
      <div class="toggle-grid switch-grid">
        ${switchControl("sm-launch", "启动游戏", "任务开始前主动启动游戏", d.launchGame, "toggle-sm-flag", 'data-flag="launch"')}
        ${switchControl("sm-force", "强制关闭", "任务结束或失败时清理游戏进程", d.forceCloseGame, "toggle-sm-flag", 'data-flag="force"')}
        ${switchControl("sm-autoupdate", "自动更新配置", "运行结束时同步用户配置", true, "toggle-sm-flag", 'data-flag="autoupdate" data-testid="sm-autoupdate" disabled')}
      </div>
      <div id="sm-game-box" class="nested-panel">
        ${gameBoxHtml(d, emulatorAllowed(pluginType))}
      </div>
    </div>
    <div class="subsection"><div class="section-heading"><h3>运行设置</h3></div>
      <div class="form-grid three">
        ${valueField("sm-attempts", "最大尝试次数（含首次） <span class='req'>*</span>", d.maxAttempts, "number", `min="${l.minAttempts ?? 1}" max="${l.maxAttempts ?? 10}"`, "每次任务最多尝试的次数，包含首次运行。")}
        ${valueField("sm-stall", "日志无更新上限（分钟） <span class='req'>*</span>", d.logStallTimeoutMinutes, "number", `min="-1" max="${l.maxStallMinutes ?? 60}"`, "日志无更新达到此分钟数后判定当前尝试停滞；填 -1 表示长时脚本。")}
        ${valueField("sm-total", "运行总时间上限（分钟） <span class='req'>*</span>", d.totalTimeoutMinutes, "number", `min="-1" max="${l.maxTotalMinutes ?? 720}"`, "限制整次任务总运行时间；长时脚本可填 -1（永不超时），普通脚本需填写有效分钟数；总时间包含全部重试以及任务前/后脚本。")}
      </div>
    </div>`
    : `<div class="form-grid">
      ${valueField("sm-name", "脚本名称 <span class='req'>*</span>", d.name)}
      ${pathField("sm-root", "脚本根目录 <span class='req'>*</span>", d.rootPath, "folder", 'placeholder="脚本根目录"')}
    </div>
    <div class="form-grid">
      ${pathField("sm-exe", "脚本主程序路径 <span class='req'>*</span>", d.mainExe, "file", 'placeholder="脚本主程序文件"', "可执行文件|*.exe;*.bat;*.cmd;*.com|所有文件|*.*", 'data-path-root-target="sm-root" data-path-root-error="脚本主程序路径错误"', "选择主程序后仍可手动修改路径。")}
      ${valueField("sm-args", "脚本自启动参数", d.args, "text", 'placeholder="可选启动参数"', "可选；如 -x --mode=1；若以路径开头（如 .\\app.exe?-args），问号后的内容作为执行参数。")}
    </div>
    <div class="form-grid">
      ${pathField("sm-config", "配置文件路径/文件夹 <span class='req'>*</span>", d.configPath, "file-or-folder", 'placeholder="请先填写脚本根目录"', "", 'data-path-root-target="sm-root" data-path-root-error="脚本根目录错误"', "配置路径相对于脚本根目录，可选择配置文件或配置文件夹。")}
      ${pathField("sm-log", "日志路径（支持日期占位符与通配符） <span class='req'>*</span>", d.logPath, "file-or-folder", 'placeholder="日志文件路径"', "日志文件|*.log;*.txt|所有文件|*.*", 'data-path-root-target="sm-root" data-path-root-error="脚本根目录错误"', "支持日期占位符与通配符；例如 D:\\Scripts\\logs\\{YYYY-MM-DD}.log，或 D:\\Scripts\\logs\\*.log。")}
    </div>
    <div class="subsection"><div class="section-heading"><h3>游戏联动设置</h3></div>
      <div class="toggle-grid switch-grid">
        ${switchControl("sm-launch", "启动游戏", "任务开始前主动启动游戏", d.launchGame, "toggle-sm-flag", 'data-flag="launch"')}
        ${switchControl("sm-force", "强制关闭", "任务结束或失败时清理游戏进程", d.forceCloseGame, "toggle-sm-flag", 'data-flag="force"')}
        ${switchControl("sm-autoupdate", "自动更新配置", "运行结束时同步用户配置", d.autoUpdateConfig, "toggle-sm-flag", 'data-flag="autoupdate" data-testid="sm-autoupdate"')}
      </div>
      <div id="sm-game-box" class="nested-panel">
        ${gameBoxHtml(d, emulatorAllowed(pluginType))}
      </div>
    </div>
    <div class="subsection"><div class="section-heading"><h3>运行设置</h3></div>
      <div class="form-grid three">
        ${valueField("sm-attempts", "最大尝试次数（含首次） <span class='req'>*</span>", d.maxAttempts, "number", `min="${l.minAttempts ?? 1}" max="${l.maxAttempts ?? 10}"`, "每次任务最多尝试的次数，包含首次运行。")}
        ${valueField("sm-stall", "日志无更新上限（分钟） <span class='req'>*</span>", d.logStallTimeoutMinutes, "number", `min="-1" max="${l.maxStallMinutes ?? 60}"`, "日志无更新达到此分钟数后判定当前尝试停滞；填 -1 表示长时脚本。")}
        ${valueField("sm-total", "运行总时间上限（分钟） <span class='req'>*</span>", d.totalTimeoutMinutes, "number", `min="-1" max="${l.maxTotalMinutes ?? 720}"`, "限制整次任务总运行时间；长时脚本可填 -1（永不超时），普通脚本需填写有效分钟数；总时间包含全部重试以及任务前/后脚本。")}
      </div>
      <div class="subsection judge-box"><div class="section-heading"><h3>自定义完成标志</h3></div>
        <div id="sm-kw-box" ${d.judgeScriptEnabled ? "hidden" : ""}>
          <div class="field" data-help="每行是一组条件；组内用逗号表示 AND，多行表示 OR。留空表示不判定成功。"><label class="field-label" for="sm-succ-kw">成功关键字</label>
          <textarea id="sm-succ-kw" placeholder="逗号表示 AND，多行表示 OR">${esc(d.successKeywords)}</textarea></div>
          <div class="field" data-help="任一失败关键字命中即终止本次尝试，并按最大尝试次数重试。语法与成功关键字相同。"><label class="field-label" for="sm-fail-kw">失败关键字</label>
          <textarea id="sm-fail-kw" placeholder="命中即判定失败">${esc(d.failureKeywords)}</textarea></div>
        </div>
        <div id="sm-script-box" ${d.judgeScriptEnabled ? "" : "hidden"}>
          ${selectField("sm-judge-lang", "判断脚本语言", d.judgeScriptLanguage === "python" ? "python" : "javascript", [{ value: "javascript", label: "JavaScript（内置引擎）" }, { value: "python", label: "Python（系统解释器）" }], "", "选择判断脚本执行语言；JavaScript 使用内置引擎，Python 使用系统解释器。")}
           <div class="field" data-help="输入包含本次尝试日志段与 screenshots 截图元数据：JavaScript 用 __NEXUS_INPUT__ 读取，Python 用 sys.argv[1] 读取路径。nexus.readFile 只读 config/script 目录，nexus.writeFile 与 nexus.listFiles 操作 script 目录；JavaScript 可调用 nexus.captureScreenshot()，Python 可使用临时 screenshotApi。输出可用 notifyScreenshotId 选择通知截图。无输出或缺少 status/reason 会继续运行。"><label class="field-label" for="sm-judge-code">判断脚本代码</label><textarea id="sm-judge-code" class="mono code-area" placeholder="输出 JSON 结果">${esc(d.judgeScript)}</textarea></div>
        </div>
        <div class="judge-actions">
          <button class="judge-upload-button" type="button" data-action="upload-judge-script" id="sm-upload-btn" ${d.judgeScriptEnabled ? "" : "hidden"}>上传脚本文件</button>
          <button class="judge-mode-card mode-toggle" type="button" data-action="toggle-judge-mode" id="sm-mode-btn" data-help="启用判断脚本后，脚本输出优先用于确定运行结果；关闭后使用成功关键字和失败关键字。" data-toggle-text="false" data-hint="脚本优先" aria-label="使用判断脚本，脚本优先" aria-pressed="${d.judgeScriptEnabled ? "true" : "false"}">使用判断脚本<span class="judge-toggle-track" aria-hidden="true"><span class="judge-toggle-thumb"></span></span></button>
        </div>
      </div>
    </div>`;
  const footer = '<button class="ghost" type="button" data-action="close-modal">取消</button><button class="primary" type="button" data-action="save-script">保存</button>';
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
    toast("无法从该根目录推导专项配置：" + error.message, "error");
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
  if (stateText) stateText.textContent = pressed ? "已启用" : "已停用";
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
      toast("脚本文件过大（上限 256KB）", "error");
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
      toast(`已载入脚本（${lang === "python" ? "Python" : "JavaScript"}）`);
      finish();
    };
    reader.onerror = () => {
      toast("读取脚本文件失败", "error");
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
    ? [["sm-name", "脚本名称"], ["sm-root", "脚本根目录"], ["sm-attempts", "最大尝试次数"], ["sm-stall", "日志无更新上限"], ["sm-total", "运行总时间上限"]]
    : [["sm-name", "脚本名称"], ["sm-root", "脚本根目录"], ["sm-exe", "脚本主程序路径"], ["sm-config", "配置文件路径"], ["sm-log", "日志路径"], ["sm-attempts", "最大尝试次数"], ["sm-stall", "日志无更新上限"], ["sm-total", "运行总时间上限"]];
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
  if (firstError) { toast("请完善表单中的必填项", "error"); return; }
  const l = state.limits || {};
  const ILLEGAL_PATH = /["<>|?*{}]/;
  const ILLEGAL_LOG = /["<>|?]/;
  const pathFields = isSpecial
    ? [["sm-root", "脚本根目录", ILLEGAL_PATH]]
    : [["sm-root", "脚本根目录", ILLEGAL_PATH], ["sm-exe", "脚本主程序路径", ILLEGAL_PATH], ["sm-config", "配置文件路径/文件夹", ILLEGAL_PATH], ["sm-log", "日志路径（支持日期占位符与通配符）", ILLEGAL_LOG]];
  for (const [id, label, illegal] of pathFields) {
    const value = stripQuotes($dom("#" + id)?.value);
    if (illegal.test(value)) {
      setFieldError(id, `${label}包含非法字符`);
      toast(`${label}包含非法字符`, "error");
      return;
    }
  }
  const nameBytes = new TextEncoder().encode($dom("#sm-name").value.trim()).length;
  if (nameBytes > MAX_ENTITY_NAME_BYTES) {
    setFieldError("sm-name", `脚本名称最多 ${MAX_ENTITY_NAME_BYTES} 字节`);
    toast(`脚本名称最多 ${MAX_ENTITY_NAME_BYTES} 字节`, "error");
    return;
  }
  const name = $dom("#sm-name").value.trim();
  if (hasEntityNameConflict(state.scripts, name, scriptDraft.id)) {
    setFieldInvalid("sm-name");
    toast("脚本名称已存在，请使用其他名称", "error");
    return;
  }
  const attempts = parseInt($dom("#sm-attempts")?.value, 10);
  const stall = parseInt($dom("#sm-stall")?.value, 10);
  const total = parseInt($dom("#sm-total")?.value, 10);
  if (!(attempts >= (l.minAttempts ?? 1)) || !(attempts <= (l.maxAttempts ?? 10))) {
    setFieldError("sm-attempts", `最大尝试次数须在 ${l.minAttempts ?? 1}-${l.maxAttempts ?? 10} 之间`);
    toast(`最大尝试次数须在 ${l.minAttempts ?? 1}-${l.maxAttempts ?? 10} 之间`, "error");
    return;
  }
  // 日志无更新上限为 -1 定义长时脚本；普通脚本不能禁用运行总时间上限
  const longStall = stall === -1;
  const unlimitedTotal = total === -1;
  if (!longStall && unlimitedTotal) {
    setFieldError("sm-total", "日志无更新上限未填 -1 时，运行总时间上限不能填 -1");
    toast("日志无更新上限未填 -1 时，运行总时间上限不能填 -1", "error");
    return;
  }
  if (!longStall && (!(stall >= (l.minStallMinutes ?? 1)) || !(stall <= (l.maxStallMinutes ?? 60)))) {
    setFieldError("sm-stall", `日志无更新超时须在 ${l.minStallMinutes ?? 1}-${l.maxStallMinutes ?? 60} 分钟之间`);
    toast(`日志无更新超时须在 ${l.minStallMinutes ?? 1}-${l.maxStallMinutes ?? 60} 分钟之间`, "error");
    return;
  }
  if (!unlimitedTotal && (!(total >= (l.minTotalMinutes ?? 5)) || !(total <= (l.maxTotalMinutes ?? 720)))) {
    setFieldError("sm-total", `运行总时间超时须在 ${l.minTotalMinutes ?? 5}-${l.maxTotalMinutes ?? 720} 分钟之间`);
    toast(`运行总时间超时须在 ${l.minTotalMinutes ?? 5}-${l.maxTotalMinutes ?? 720} 分钟之间`, "error");
    return;
  }
  const judgeEnabled = ($dom("#sm-mode-btn")?.getAttribute("aria-pressed") ?? "false") === "true";
  const judgeCode = $dom("#sm-judge-code")?.value ?? "";
  if (judgeEnabled && !judgeCode.trim()) {
    setRequiredFieldError("sm-judge-code");
    toast("请填写判断脚本代码，或关闭「使用脚本」", "error");
    return;
  }
  const launchGame = $dom("#sm-launch")?.getAttribute("aria-pressed") === "true";
  const gameMode = $dom("#sm-mode")?.value === "emulator" ? "emulator" : "pc";
  const selfManagedPc = selfManagedPcLaunch(scriptDraft.pluginType) && gameMode !== "emulator";
  const gameExe = stripQuotes($dom("#sm-game-exe")?.value);
  if (!gameExe) {
    setRequiredFieldError("sm-game-exe");
    toast(gameMode === "emulator" ? "请填写模拟器ADB地址" : "请填写游戏路径", "error");
    return;
  }
  if (gameMode === "emulator") {
    const colon = gameExe.lastIndexOf(":");
    const port = parseInt(gameExe.slice(colon + 1), 10);
    if (colon <= 0 || !(port >= 1 && port <= 65535)) {
      setFieldError("sm-game-exe", "模拟器ADB地址格式不正确（应为 主机:端口，如 127.0.0.1:16384）");
      toast("模拟器ADB地址格式不正确（应为 主机:端口，如 127.0.0.1:16384）", "error");
      return;
    }
  } else if (ILLEGAL_PATH.test(gameExe)) {
    setFieldError("sm-game-exe", "游戏路径包含非法字符");
    toast("游戏路径包含非法字符", "error");
    return;
  }
  const payload = {
    id: scriptDraft.id, pluginType: scriptDraft.pluginType || "", name, rootPath: stripQuotes($dom("#sm-root")?.value),
    pluginInputs: isSpecial ? collectPluginInputs() : {},
    mainExe: isSpecial ? "" : stripQuotes($dom("#sm-exe")?.value), args: isSpecial ? "" : $dom("#sm-args").value.trim(),
    configPath: isSpecial ? "" : stripQuotes($dom("#sm-config")?.value), logPath: isSpecial ? "" : stripQuotes($dom("#sm-log")?.value),
    launchGame: selfManagedPc ? false : launchGame, gameMode, gameExe, gameArgs: selfManagedPc ? "" : ($dom("#sm-game-args")?.value.trim() || ""), gameWaitSeconds: selfManagedPc ? 30 : (+($dom("#sm-game-wait")?.value || 0) || 0),    forceCloseGame: $dom("#sm-force")?.getAttribute("aria-pressed") === "true", maxAttempts: attempts, logStallTimeoutMinutes: stall, totalTimeoutMinutes: total,
    successKeywords: isSpecial ? "" : ($dom("#sm-succ-kw")?.value ?? ""), failureKeywords: isSpecial ? "" : ($dom("#sm-fail-kw")?.value ?? ""),
    judgeScriptEnabled: judgeEnabled, judgeScriptLanguage: $dom("#sm-judge-lang")?.value || "", judgeScript: judgeCode,
    autoUpdateConfig: isSpecial ? true : ($dom("#sm-autoupdate")?.getAttribute("aria-pressed") === "true"),
  };
  try {
    let saved;
    if (payload.id) saved = await api("PUT", "/api/scripts/" + payload.id, payload);
    else saved = await api("POST", "/api/scripts", payload);
    closeModal();
    toast("脚本实例已保存");
    applySaveValidation(saved?.validation);
    const token = state.routeToken;
    await pageScripts(token);
  } catch (error) {
    if (error?.code === "duplicate_name") {
      setFieldInvalid("sm-name");
      toast("脚本名称已存在，请使用其他名称", "error");
      return;
    }
    toast(error.message, "error");
  }
}

/** 保存脚本实例响应中的专项校验结果：角落通知提醒配置差异（通知为提醒性质，配置不被修改）。 */
function applySaveValidation(validation) {
  if (!validation) return;
  if (validation.error) {
    toast("专项插件配置校验执行失败。", "error");
  }
  for (const item of validation.notifications || []) {
    pushNotice(item.title || "", item.body || "", item.kind || "info");
  }
  for (const item of validation.toasts || []) {
    toast(item.message || "", item.kind || "info");
  }
}

export function deleteScript(id, name) {
  confirmModal("删除脚本实例", `确定删除脚本实例「${esc(name)}」？此操作不可恢复。`, "confirm-delete-script", { id, name });
}

export async function confirmDeleteScript(id, name) {
  try { await api("DELETE", "/api/scripts/" + id); closeModal(); toast("脚本实例已删除"); await pageScripts(state.routeToken); }
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
