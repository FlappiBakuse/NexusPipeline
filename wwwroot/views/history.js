import { api } from "../core/api.js";
import { esc, finalStatusOf, fmtTime, statusBadge } from "../core/format.js";
import { pagerMarkup, registerPager } from "../core/pager.js";
import { isCurrent, state } from "../core/state.js";
import { modalShell, showModal } from "../core/modal.js";
import { navActive, render, setTopbarTitle, toast, withBusy } from "../core/ui.js";

let historyPage = 1;
let historyDays = 7;
const HISTORY_PAGE_SIZE = 20;
const HISTORY_DAY_OPTIONS = [7, 30, 180];

export async function pageHistory(token) {
  if (!isCurrent("history", token)) return;
  navActive("history"); setTopbarTitle("历史记录");
  let data, scripts, queues;
  try { [data, scripts, queues] = await Promise.all([api("GET", `/api/history?days=${historyDays}&offset=${(historyPage - 1) * HISTORY_PAGE_SIZE}&limit=${HISTORY_PAGE_SIZE}`), api("GET", "/api/scripts"), api("GET", "/api/queues")]); }
  catch (error) { render(`<div class="empty"><strong>加载历史记录失败</strong>${esc(error.message)}</div>`); return; }
  if (!isCurrent("history", token)) return;
  const records = data.records || data;
  const total = data.total ?? records.length;
  const scriptName = id => scripts.find(script => script.id === id)?.name || "(已删除)";
  const queueName = id => queues.find(queue => queue.id === id)?.name || "";
  const content = records.length ? `<section class="card"><div class="table-scroll"><table class="data-table"><thead><tr><th scope="col">时间</th><th scope="col">脚本</th><th scope="col">队列</th><th scope="col">模式</th><th scope="col">结果</th><th scope="col">操作</th></tr></thead><tbody>${records.map(record => `<tr><td>${esc(fmtTime(record.startTime))}</td><td>${esc(scriptName(record.scriptInstanceId))}</td><td>${esc(queueName(record.queueId)) || "-"}</td><td>${record.mode === "auto" ? "自动" : "手动"}</td><td>${statusBadge(finalStatusOf(record))}</td><td class="ops"><button class="sm" type="button" data-action="history-detail" data-id="${esc(record.id)}">查看详情</button></td></tr>`).join("")}</tbody></table></div>${pagerMarkup("history", historyPage, HISTORY_PAGE_SIZE, total)}</section>` : '<div class="empty"><strong>暂无历史记录</strong>运行脚本或调度队列后在此查看。</div>';
  render(`<div class="page-head"><div><div class="eyebrow">历史记录</div><h2>历史记录（最近 ${historyDays} 天，共 ${total} 条）</h2><p class="page-kicker">按运行时间查看结果、重试过程和脚本输出。</p></div><div class="history-days-box"><label class="field-label" for="history-days">天数范围</label><select id="history-days" data-action="history-days" data-testid="history-days">${HISTORY_DAY_OPTIONS.map(days => `<option value="${days}" ${days === historyDays ? "selected" : ""}>${days} 天</option>`).join("")}</select></div></div>${content}`);
  registerPager("history", page => { historyPage = page; pageHistory(state.routeToken); });
}

/** 历史天数范围切换（v0.6.3+）：重置分页并重新拉取。 */
export function historyDaysChange(target) {
  const days = Number(target.value) || 7;
  if (days === historyDays) return;
  historyDays = days;
  historyPage = 1;
  pageHistory(state.routeToken);
}

function historyLogMarkup(id, attemptKey, logInfo, label) {
  const total = logInfo?.logTotalLines || 0;
  const full = logInfo?.logText != null;
  const tailNote = total > 200 && !full ? "，仅显示尾部 200 行" : "";
  const action = total > 200 && !full
    ? `<div class="history-log-actions"><span class="muted">日志较长，默认只加载尾部</span><button class="ghost sm" type="button" data-action="history-full-log" data-id="${esc(id)}" data-attempt="${esc(attemptKey)}">查看完整日志</button></div>`
    : "";
  const text = full ? logInfo.logText : (logInfo?.logTail || "（无脚本日志）");
  return `<div class="history-log" data-history-log data-attempt="${esc(attemptKey)}"><div class="qk-row" data-history-log-meta>${label}${logInfo ? `，${total} 行${tailNote}` : ""}</div>${action}<pre class="logbox" data-history-log-body>${esc(text)}</pre></div>`;
}

export async function historyDetail(id) {
  try {
    const data = await api("GET", "/api/history/detail?id=" + encodeURIComponent(id));
    const record = data.record;
    if (!record) return;
    const attempts = (record.attemptDetails || []).map(attempt => {
      const logInfo = (data.attemptLogs || []).find(l => l.number === attempt.number);
      return `<div class="subsection"><h3>第 ${attempt.number} 次尝试：${attempt.status === "success" ? "成功" : attempt.status === "cancelled" ? "已取消" : "失败"}</h3><div class="detail"><div class="kv"><span class="k">原因</span><span>${esc(attempt.reason || "-")}</span></div><div class="kv"><span class="k">时间</span><span>${esc(fmtTime(attempt.startTime))} - ${esc(fmtTime(attempt.endTime))}</span></div></div>${historyLogMarkup(id, String(attempt.number), logInfo, `脚本日志（第 ${attempt.number} 次尝试）`)} </div>`;
    }).join("");
    const legacySection = data.legacyLog ? `<div class="subsection"><h3>兼容旧格式日志</h3>${historyLogMarkup(id, "legacy", data.legacyLog, "脚本日志")}</div>` : "";
    const body = `<div class="detail"><div class="kv"><span class="k">开始</span><span>${esc(fmtTime(record.startTime))}</span></div><div class="kv"><span class="k">结束</span><span>${esc(fmtTime(record.endTime))}</span></div><div class="kv"><span class="k">模式</span><span>${record.mode === "auto" ? "自动运行" : "手动运行"}</span></div>${record.userName ? `<div class="kv"><span class="k">用户</span><span>${esc(record.userName)}</span></div>` : ""}<div class="kv"><span class="k">重试</span><span>${record.attempts || 0} / ${record.maxAttempts || "-"} 次</span></div><div class="kv"><span class="k">结果</span><span>${statusBadge(finalStatusOf(record))} ${esc(record.resultDetail)}</span></div></div>${attempts}${legacySection}`;
    showModal(modalShell(`${esc(record.scriptName)} 运行详情`, body, '<button class="ghost" type="button" data-action="close-modal">关闭</button>'), true);
  } catch (error) { toast(error.message, "error"); }
}

export async function historyFullLog(id, attemptKey, target) {
  try {
    const query = `/api/history/detail?id=${encodeURIComponent(id)}&full=true&attempt=${encodeURIComponent(attemptKey)}`;
    const data = await api("GET", query);
    const info = attemptKey === "legacy"
      ? data.legacyLog
      : (data.attemptLogs || []).find(log => String(log.number) === String(attemptKey));
    if (!info || info.logText == null) throw new Error("完整日志不存在或已被清理");
    const root = target.closest("[data-history-log]");
    const body = root?.querySelector("[data-history-log-body]");
    const meta = root?.querySelector("[data-history-log-meta]");
    if (!body || !meta) return;
    body.textContent = info.logText || "（无脚本日志）";
    meta.textContent = `${attemptKey === "legacy" ? "脚本日志" : `脚本日志（第 ${attemptKey} 次尝试）`}，${info.logTotalLines || 0} 行`;
    target.remove();
  } catch (error) { toast(error.message, "error"); }
}

export const actions = {
  "history-detail": target => historyDetail(target.dataset.id),
  "history-days": target => historyDaysChange(target),
  "history-full-log": target => withBusy(target, () => historyFullLog(target.dataset.id, target.dataset.attempt, target)),
};
