import { api } from "../core/api.js";
import { $ as $dom } from "../core/dom.js";
import { esc, scriptFallbackIcon } from "../core/format.js";
import { scrollField, selectField, valueField, pageHeader } from "../core/forms.js";
import { pagerMarkup, registerPager } from "../core/pager.js";
import { isCurrent, notifyAvailable, state } from "../core/state.js";
import { closeModal, confirmModal, modalShell, showModal } from "../core/modal.js";
import { navActive, render, setFieldError, clearFieldError, setTopbarTitle, toast, withBusy } from "../core/ui.js";
import { initDndList } from "../core/dnd.js";

let scriptDraft = null;
let scriptPage = 1;
const SCRIPT_PAGE_SIZE = 20;

function specializedPlugins() {
  return (state.plugins || []).filter(p => p.kind === "specialized" && p.enabled);
}

/** 启动方式选择是否可用（v0.7.0+）：「模拟器适配」插件需已启用（全局能力开关）；通用脚本恒可用；专用插件按 plugin.json 的 supportsEmulator 声明（缺省不支持）。 */
function emulatorAllowed(pluginType) {
  const adapter = (state.plugins || []).find(p => p.name === "emulator-adapter");
  if (adapter && !adapter.enabled) return false;
  if (!pluginType) return true;
  const meta = (state.plugins || []).find(p => p.name === pluginType);
  return !!meta && !!meta.supportsEmulator;
}

/** 游戏配置卡（v0.7.0+）：启动方式选择器（仅支持时渲染）+ ADB 地址/游戏路径按模式切换 + 启动参数 + 等待秒数。 */
function gameBoxHtml(d, emulatorOk) {
  const isEmu = emulatorOk && d.gameMode === "emulator";
  const modeRow = emulatorOk
    ? `<div class="form-grid game-mode-row">${selectField("sm-mode", "启动方式", isEmu ? "emulator" : "pc", [{ value: "pc", label: "PC 客户端" }, { value: "emulator", label: "安卓模拟器" }], 'data-action="change-sm-mode"')}<div class="game-wait-field">${valueField("sm-game-wait", "启动后等待秒数", d.gameWaitSeconds, "number", 'min="0"')}</div></div>`
    : `<div class="form-grid game-mode-row">${valueField("sm-game-wait", "启动后等待秒数", d.gameWaitSeconds, "number", 'min="0"')}<div class="game-wait-field" aria-hidden="true"></div></div>`;
  const exeField = isEmu
    ? valueField("sm-game-exe", "模拟器ADB地址 <span class='req'>*</span>", d.gameExe, "text", 'placeholder="例如 127.0.0.1:16384"')
    : valueField("sm-game-exe", "游戏路径 <span class='req'>*</span>", d.gameExe, "text", 'placeholder="请填写游戏可执行文件路径"');
  const argsField = isEmu
    ? valueField("sm-game-args", "启动参数", d.gameArgs, "text", 'placeholder="am start 参数，如 -n 包名/.MainActivity"')
    : valueField("sm-game-args", "启动参数", d.gameArgs);
  return `<div class="form-grid">${exeField}${argsField}</div>${modeRow}`;
}

/** 启动方式切换（v0.7.0+）：更新游戏路径/ADB 地址字段的标签与提示。 */
export function changeGameMode() {
  const isEmu = $dom("#sm-mode")?.value === "emulator";
  const exe = $dom("#sm-game-exe");
  const args = $dom("#sm-game-args");
  const exeLabel = $dom('label[for="sm-game-exe"]');
  if (exeLabel) exeLabel.innerHTML = isEmu ? "模拟器ADB地址 <span class='req'>*</span>" : "游戏路径 <span class='req'>*</span>";
  if (exe) exe.placeholder = isEmu ? "例如 127.0.0.1:16384" : "请填写游戏可执行文件路径";
  if (args) args.placeholder = isEmu ? "am start 参数，如 -n 包名/.MainActivity" : "";
}

function pluginDisplay(name) {
  const plugin = (state.plugins || []).find(p => p.name === name);
  return plugin ? plugin.displayName : name;
}

/** 徽章游戏名：专用插件提供（gameName），无则回退显示名。 */
function pluginGameName(name) {
  const plugin = (state.plugins || []).find(p => p.name === name);
  return plugin ? (plugin.gameName || plugin.displayName) : name;
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
  const notifyOn = notifyAvailable();
  const atLimit = !!(state.limits && scripts.length >= state.limits.maxScripts);
  const action = `<button type="button" data-action="open-script-modal" data-testid="new-script" ${atLimit ? "disabled" : ""}>新建脚本实例${atLimit ? `（${scripts.length}/${state.limits.maxScripts}）` : ""}</button>`;
  const totalPages = Math.max(1, Math.ceil(scripts.length / SCRIPT_PAGE_SIZE));
  if (scriptPage > totalPages) scriptPage = totalPages;
  const pageItems = scripts.slice((scriptPage - 1) * SCRIPT_PAGE_SIZE, scriptPage * SCRIPT_PAGE_SIZE);
  const content = scripts.length === 0
    ? '<div class="empty"><strong>暂无脚本实例</strong>点击右上角「新建脚本实例」创建你的第一个脚本。</div>'
    : `<section class="card"><div class="script-grid">
      ${pageItems.map(script => `<article class="script-card" data-testid="script-card" data-dnd-id="${esc(script.id)}">
        <span class="drag-handle" role="button" tabindex="0" aria-label="拖拽排序（方向键调整顺序）" title="拖拽排序">⋮⋮</span>
        <img class="script-ico" src="/api/scripts/${script.id}/icon" alt="" width="36" height="36" loading="lazy" data-fallback="${esc(scriptFallbackIcon)}">
        <div class="script-main">
          <div class="script-name-row"><strong class="scroll-text"><span class="scroll-inner">${esc(script.name)}</span></strong></div>
          <div class="script-name-row"><span class="badge ${script.pluginType ? "blue" : "muted"}">${script.pluginType ? `${esc(pluginGameName(script.pluginType))}专项` : "通用"}</span>${script.logStallTimeoutMinutes === -1 && script.totalTimeoutMinutes === -1 ? `<span class="badge warn" data-testid="script-long-badge">长时</span>` : ""}${notifyOn ? `<span class="badge ${script.notifyEnabled ? "ok" : "muted"}" data-testid="script-notify">${script.notifyEnabled ? "通知：开" : "通知：关"}</span>` : ""}</div>
        </div>
        <div class="script-ops">
          <button class="sm" type="button" data-action="manage-users" data-id="${esc(script.id)}">用户管理${(script.users || []).length ? `（${script.users.length}）` : ""}</button>
          <button class="sm" type="button" data-action="edit-script" data-id="${esc(script.id)}">编辑脚本</button>
          <button class="sm danger" type="button" data-action="delete-script" data-id="${esc(script.id)}" data-name="${esc(script.name)}">删除脚本</button>
        </div>
      </article>`).join("")}
    </div>${pagerMarkup("scripts", scriptPage, SCRIPT_PAGE_SIZE, scripts.length)}</section>`;
  render(pageHeader("脚本实例", "脚本实例", "管理脚本入口、用户配置和运行策略。", action) + content);
  registerPager("scripts", page => { scriptPage = page; pageScripts(state.routeToken); });
  wireScriptIcons();
  wireScriptDnd();
}

/** 拖拽排序（v0.6.8+）：页内重排可见项，其余项保持相对顺序追加；提交全量顺序落盘。 */
function wireScriptDnd() {
  const list = $dom(".script-grid");
  if (!list) return;
  initDndList(list, { onDrop: (ids) => reorderScripts(ids) });
}

/** 把可见项按拖拽后的顺序重排进全量列表（其余项保持原相对顺序），提交 PUT /api/scripts/order。 */
async function reorderScripts(visibleIds) {
  const visible = new Set(visibleIds);
  const byId = new Map(state.scripts.map(item => [item.id, item]));
  const ordered = visibleIds.map(id => byId.get(id)).filter(Boolean);
  const rest = state.scripts.filter(item => !visible.has(item.id));
  const full = [...ordered, ...rest];
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
  showModal(modalShell("新建脚本实例", body, footer));
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
  const value = script || {};
  const pluginType = value.pluginType || plugin || "";
  const isSpecial = !!pluginType;
  scriptDraft = {
    id: value.id || "", pluginType, name: value.name || "", rootPath: value.rootPath || "",
    mainExe: value.mainExe || "", args: value.args || "", configPath: value.configPath || "", logPath: value.logPath || "",
    launchGame: !!value.launchGame, gameMode: value.gameMode === "emulator" ? "emulator" : "pc", gameExe: value.gameExe || "", gameArgs: value.gameArgs || "",
    gameWaitSeconds: value.gameWaitSeconds ?? 30, forceCloseGame: !!value.forceCloseGame,
    maxAttempts: value.maxAttempts ?? 3, logStallTimeoutMinutes: value.logStallTimeoutMinutes ?? 5,
    totalTimeoutMinutes: value.totalTimeoutMinutes ?? 120,
    successKeywords: value.successKeywords || "", failureKeywords: value.failureKeywords || "",
    judgeScriptEnabled: !!value.judgeScriptEnabled, judgeScriptLanguage: value.judgeScriptLanguage || "", judgeScript: value.judgeScript || "",
    notifyEnabled: !!value.notifyEnabled,
  };
  const d = scriptDraft;
  const l = state.limits || {};
  const title = isSpecial
    ? (id ? `编辑${esc(pluginDisplay(pluginType))}专项脚本实例` : `新建${esc(pluginDisplay(pluginType))}专项脚本实例`)
    : (id ? "编辑脚本实例" : "新建脚本实例");
  const body = isSpecial
    ? `<div class="form-grid">
      ${valueField("sm-name", "脚本名称 <span class='req'>*</span>", d.name)}
      ${valueField("sm-root", "脚本根目录 <span class='req'>*</span>", d.rootPath, "text", 'placeholder="例如 C:\\Scripts\\YourGame"')}
    </div>
    <p class="muted helper-copy">由专用插件「${esc(pluginDisplay(pluginType))}」自动适配脚本主程序、自启动参数、配置文件与日志路径，无需手动填写。</p>
    <div class="subsection"><div class="section-heading"><h3>游戏与通知</h3><span class="muted">按需启用，不影响基础脚本执行</span></div>
      <div class="toggle-grid">
        <button class="mode-toggle" type="button" data-action="toggle-sm-flag" data-flag="launch" id="sm-launch" aria-pressed="${d.launchGame ? "true" : "false"}">启动游戏</button>
        <button class="mode-toggle" type="button" data-action="toggle-sm-flag" data-flag="force" id="sm-force" aria-pressed="${d.forceCloseGame ? "true" : "false"}">强制关闭</button>
        <button class="mode-toggle" type="button" data-action="toggle-sm-flag" data-flag="notify" id="sm-notify" ${notifyAvailable() ? "" : "hidden"} aria-pressed="${d.notifyEnabled ? "true" : "false"}">运行通知</button>
      </div>
      <p class="muted helper-copy">启动游戏：运行脚本前启动；强制关闭：运行结束后结束游戏进程；运行通知：发送状态到通知渠道。</p>
      <div id="sm-game-box" class="nested-panel">
        ${gameBoxHtml(d, emulatorAllowed(pluginType))}
      </div>
    </div>
    <div class="subsection"><div class="section-heading"><h3>运行设置</h3><span class="muted">超时后会按最大尝试次数重试</span></div>
      <div class="form-grid three">
        ${valueField("sm-attempts", "最大尝试次数（含首次） <span class='req'>*</span>", d.maxAttempts, "number", `min="${l.minAttempts ?? 1}" max="${l.maxAttempts ?? 10}"`)}
        ${valueField("sm-stall", "日志无更新超时（分钟） <span class='req'>*</span>", d.logStallTimeoutMinutes, "number", `min="${l.minStallMinutes ?? 1}" max="${l.maxStallMinutes ?? 60}" placeholder="-1 = 不超时（长时脚本）"`)}
        ${valueField("sm-total", "运行总时间超时（分钟） <span class='req'>*</span>", d.totalTimeoutMinutes, "number", `min="${l.minTotalMinutes ?? 5}" max="${l.maxTotalMinutes ?? 720}" placeholder="-1 = 不超时（长时脚本）"`)}
      </div>
      <p class="muted helper-copy">两个超时均填 -1 即为长时脚本（无限等待）；两者须同为 -1 才能保存，且长时脚本不能与普通脚本编排进同一调度队列。</p>
    </div>`
    : `<div class="form-grid">
      ${valueField("sm-name", "脚本名称 <span class='req'>*</span>", d.name)}
      ${valueField("sm-root", "脚本根目录 <span class='req'>*</span>", d.rootPath, "text", 'placeholder="例如 C:\\Scripts\\Daily"')}
    </div>
    <div class="form-grid">
      ${valueField("sm-exe", "脚本主程序路径 <span class='req'>*</span>", d.mainExe, "text", 'placeholder="请先填写脚本根目录"')}
      ${scrollField("sm-args", "脚本自启动参数", d.args, "可选；如 -x --mode=1；以路径开头（如 .\\app.exe?-args）时 ? 后为执行端参数")}
    </div>
    <div class="form-grid">
      ${valueField("sm-config", "配置文件路径/文件夹 <span class='req'>*</span>", d.configPath, "text", 'placeholder="请先填写脚本根目录"')}
      ${scrollField("sm-log", "日志路径（支持日期占位符与通配符） <span class='req'>*</span>", d.logPath, "例如 D:\\Scripts\\logs\\{YYYY-MM-DD}.log 或 …\\log.txt")}
    </div>
    <div class="subsection"><div class="section-heading"><h3>游戏与通知</h3><span class="muted">按需启用，不影响基础脚本执行</span></div>
      <div class="toggle-grid">
        <button class="mode-toggle" type="button" data-action="toggle-sm-flag" data-flag="launch" id="sm-launch" aria-pressed="${d.launchGame ? "true" : "false"}">启动游戏</button>
        <button class="mode-toggle" type="button" data-action="toggle-sm-flag" data-flag="force" id="sm-force" aria-pressed="${d.forceCloseGame ? "true" : "false"}">强制关闭</button>
        <button class="mode-toggle" type="button" data-action="toggle-sm-flag" data-flag="notify" id="sm-notify" ${notifyAvailable() ? "" : "hidden"} aria-pressed="${d.notifyEnabled ? "true" : "false"}">运行通知</button>
      </div>
      <p class="muted helper-copy">启动游戏：运行脚本前启动；强制关闭：运行结束后结束游戏进程；运行通知：发送状态到通知渠道。</p>
      <div id="sm-game-box" class="nested-panel">
        ${gameBoxHtml(d, emulatorAllowed(pluginType))}
      </div>
    </div>
    <div class="subsection"><div class="section-heading"><h3>运行设置</h3><span class="muted">超时后会按最大尝试次数重试</span></div>
      <div class="form-grid three">
        ${valueField("sm-attempts", "最大尝试次数（含首次） <span class='req'>*</span>", d.maxAttempts, "number", `min="${l.minAttempts ?? 1}" max="${l.maxAttempts ?? 10}"`)}
        ${valueField("sm-stall", "日志无更新超时（分钟） <span class='req'>*</span>", d.logStallTimeoutMinutes, "number", `min="${l.minStallMinutes ?? 1}" max="${l.maxStallMinutes ?? 60}" placeholder="-1 = 不超时（长时脚本）"`)}
        ${valueField("sm-total", "运行总时间超时（分钟） <span class='req'>*</span>", d.totalTimeoutMinutes, "number", `min="${l.minTotalMinutes ?? 5}" max="${l.maxTotalMinutes ?? 720}" placeholder="-1 = 不超时（长时脚本）"`)}
      </div>
      <p class="muted helper-copy">两个超时均填 -1 即为长时脚本（无限等待）；两者须同为 -1 才能保存，且长时脚本不能与普通脚本编排进同一调度队列。</p>
      <div class="subsection judge-box"><div class="section-heading"><h3>自定义完成标志</h3><span class="muted">关键字与判断脚本二选一，配置脚本时脚本优先</span></div>
        <div id="sm-kw-box" ${d.judgeScriptEnabled ? "hidden" : ""}>
          <label class="field-label" for="sm-succ-kw">成功关键字</label>
          <textarea id="sm-succ-kw" placeholder="每行一组：组内逗号分隔为 AND（整个日志中分别出现即命中），换行之间为 OR；留空表示不判定成功">${esc(d.successKeywords)}</textarea>
          <label class="field-label" for="sm-fail-kw">失败关键字</label>
          <textarea id="sm-fail-kw" placeholder="命中即判定失败并终止本次尝试，按最大尝试次数重试；语法同成功关键字">${esc(d.failureKeywords)}</textarea>
        </div>
        <div id="sm-script-box" ${d.judgeScriptEnabled ? "" : "hidden"}>
          <label class="field-label" for="sm-judge-lang">判断脚本语言</label>
          <select id="sm-judge-lang"><option value="javascript" ${d.judgeScriptLanguage === "python" ? "" : "selected"}>JavaScript（内置引擎）</option><option value="python" ${d.judgeScriptLanguage === "python" ? "selected" : ""}>Python（系统解释器）</option></select>
          <label class="field-label" for="sm-judge-code">判断脚本代码</label>
          <textarea id="sm-judge-code" class="mono code-area" placeholder="输出一行 JSON：{&quot;status&quot;:&quot;success|failed&quot;,&quot;reason&quot;:&quot;原因&quot;,&quot;notifyText&quot;:&quot;可选&quot;,&quot;replaceConfigs&quot;:[&quot;相对script目录文件&quot;]}">${esc(d.judgeScript)}</textarea>
          <p class="muted helper-copy">输入含本次尝试日志段（JavaScript 用 __NEXUS_INPUT__ 读取，Python 用 sys.argv[1] 路径）；nexus.readFile 只读 config/script 目录、nexus.writeFile/nexus.listFiles 操作 script 目录；无输出或缺 status/reason 视为继续运行。</p>
        </div>
        <div class="judge-actions">
          <button class="ghost sm" type="button" data-action="upload-judge-script" id="sm-upload-btn" ${d.judgeScriptEnabled ? "" : "hidden"}>上传脚本文件</button>
          <button class="sm mode-toggle" type="button" data-action="toggle-judge-mode" id="sm-mode-btn" data-toggle-text="false" aria-pressed="${d.judgeScriptEnabled ? "true" : "false"}">使用判断脚本（脚本优先）</button>
        </div>
      </div>
    </div>`;
  const footer = '<button type="button" data-action="save-script">保存</button><button class="ghost" type="button" data-action="close-modal">取消</button>';
  showModal(modalShell(title, body, footer));
  syncScriptGhostState();
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
    await api("POST", "/api/scripts/probe", { rootPath, pluginType });
  } catch (error) {
    toast("无法从该根目录推导专项配置：" + error.message, "error");
  }
}

export function syncScriptGhostState() {
  const root = $dom("#sm-root");
  const hasRoot = !!(root && root.value.trim());
  ["sm-exe", "sm-args", "sm-config", "sm-log"].forEach(id => {
    const element = $dom("#" + id);
    if (element) element.disabled = !hasRoot;
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

/** 切换游戏/通知开关按钮状态（启动游戏｜强制关闭｜运行通知）。 */
function toggleSmFlag(flag) {
  const btn = $dom("#sm-" + flag);
  if (!btn) return;
  btn.setAttribute("aria-pressed", btn.getAttribute("aria-pressed") === "true" ? "false" : "true");
}

/** 上传判断脚本文件：读取内容填入代码框，按扩展名自动识别语言（.py=Python，其余=JavaScript）。 */
export function uploadJudgeScript() {
  const input = document.createElement("input");
  input.type = "file";
  input.accept = ".js,.py";
  input.addEventListener("change", () => {
    const file = input.files?.[0];
    if (!file) return;
    if (file.size > 256 * 1024) {
      toast("脚本文件过大（上限 256KB）", "error");
      return;
    }
    const reader = new FileReader();
    reader.onload = () => {
      const lang = file.name.toLowerCase().endsWith(".py") ? "python" : "javascript";
      const code = $dom("#sm-judge-code");
      const language = $dom("#sm-judge-lang");
      if (code) code.value = String(reader.result || "");
      if (language) language.value = lang;
      toast(`已载入脚本（${lang === "python" ? "Python" : "JavaScript"}）`);
    };
    reader.readAsText(file, "utf-8");
  });
  input.click();
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
  const isSpecial = !!scriptDraft.pluginType;
  const required = isSpecial
    ? [["sm-name", "脚本名称"], ["sm-root", "脚本根目录"]]
    : [["sm-name", "脚本名称"], ["sm-root", "脚本根目录"], ["sm-exe", "脚本主程序路径"], ["sm-config", "配置文件路径"], ["sm-log", "日志路径"]];
  let firstError = null;
  for (const [id, label] of required) {
    const element = $dom("#" + id);
    if (!element?.value.trim()) {
      setFieldError(id, "请填写" + label);
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
  if (l.maxScriptNameBytes && nameBytes > l.maxScriptNameBytes) {
    setFieldError("sm-name", `脚本名称最多 ${l.maxScriptNameBytes} 字节`);
    toast(`脚本名称最多 ${l.maxScriptNameBytes} 字节`, "error");
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
  // v0.7.0：-1 = 不超时（长时脚本），必须成对出现
  const longStall = stall === -1;
  const longTotal = total === -1;
  if (longStall !== longTotal) {
    setFieldError("sm-stall", "长时脚本需将「日志无更新超时」与「运行总时间超时」都设为 -1（-1 = 不超时）");
    toast("长时脚本需将「日志无更新超时」与「运行总时间超时」都设为 -1（-1 = 不超时）", "error");
    return;
  }
  if (!longStall && (!(stall >= (l.minStallMinutes ?? 1)) || !(stall <= (l.maxStallMinutes ?? 60)))) {
    setFieldError("sm-stall", `日志无更新超时须在 ${l.minStallMinutes ?? 1}-${l.maxStallMinutes ?? 60} 分钟之间`);
    toast(`日志无更新超时须在 ${l.minStallMinutes ?? 1}-${l.maxStallMinutes ?? 60} 分钟之间`, "error");
    return;
  }
  if (!longTotal && (!(total >= (l.minTotalMinutes ?? 5)) || !(total <= (l.maxTotalMinutes ?? 720)))) {
    setFieldError("sm-total", `运行总时间超时须在 ${l.minTotalMinutes ?? 5}-${l.maxTotalMinutes ?? 720} 分钟之间`);
    toast(`运行总时间超时须在 ${l.minTotalMinutes ?? 5}-${l.maxTotalMinutes ?? 720} 分钟之间`, "error");
    return;
  }
  const judgeEnabled = ($dom("#sm-mode-btn")?.getAttribute("aria-pressed") ?? "false") === "true";
  const judgeCode = $dom("#sm-judge-code")?.value ?? "";
  if (judgeEnabled && !judgeCode.trim()) {
    setFieldError("sm-judge-code", "请填写判断脚本代码，或关闭「使用脚本」");
    toast("请填写判断脚本代码，或关闭「使用脚本」", "error");
    return;
  }
  const launchGame = $dom("#sm-launch")?.getAttribute("aria-pressed") === "true";
  const gameMode = $dom("#sm-mode")?.value === "emulator" ? "emulator" : "pc";
  const gameExe = stripQuotes($dom("#sm-game-exe")?.value);
  if (!gameExe) {
    setFieldError("sm-game-exe", gameMode === "emulator" ? "请填写模拟器ADB地址" : "请填写游戏路径");
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
    id: scriptDraft.id, pluginType: scriptDraft.pluginType || "", name: $dom("#sm-name").value.trim(), rootPath: stripQuotes($dom("#sm-root")?.value),
    mainExe: isSpecial ? "" : stripQuotes($dom("#sm-exe")?.value), args: isSpecial ? "" : $dom("#sm-args").value.trim(),
    configPath: isSpecial ? "" : stripQuotes($dom("#sm-config")?.value), logPath: isSpecial ? "" : stripQuotes($dom("#sm-log")?.value),
    launchGame, gameMode, gameExe, gameArgs: $dom("#sm-game-args")?.value.trim() || "", gameWaitSeconds: +($dom("#sm-game-wait")?.value || 0) || 0,
    forceCloseGame: $dom("#sm-force")?.getAttribute("aria-pressed") === "true", maxAttempts: attempts, logStallTimeoutMinutes: stall, totalTimeoutMinutes: total,
    successKeywords: isSpecial ? "" : ($dom("#sm-succ-kw")?.value ?? ""), failureKeywords: isSpecial ? "" : ($dom("#sm-fail-kw")?.value ?? ""),
    judgeScriptEnabled: judgeEnabled, judgeScriptLanguage: $dom("#sm-judge-lang")?.value || "", judgeScript: judgeCode,
    notifyEnabled: $dom("#sm-notify")?.getAttribute("aria-pressed") === "true" || !!scriptDraft.notifyEnabled,
  };
  try {
    if (payload.id) await api("PUT", "/api/scripts/" + payload.id, payload);
    else await api("POST", "/api/scripts", payload);
    closeModal();
    toast("脚本实例已保存");
    const token = state.routeToken;
    await pageScripts(token);
  } catch (error) { toast(error.message, "error"); }
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
  "edit-script": target => openScriptModal(target.dataset.id),
  "delete-script": target => deleteScript(target.dataset.id, target.dataset.name),
  "confirm-delete-script": target => withBusy(target, () => confirmDeleteScript(target.dataset.id, target.dataset.name)),
  "save-script": target => withBusy(target, () => saveScript()),
  "change-sm-mode": () => changeGameMode(),
  "upload-judge-script": () => uploadJudgeScript(),
  "toggle-judge-mode": () => toggleJudgeMode(),
  "toggle-sm-flag": target => toggleSmFlag(target.dataset.flag),
};
