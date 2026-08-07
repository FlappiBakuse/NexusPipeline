import { api } from "../core/api.js";
import { esc, finalStatusOf, fmtTime, statusBadge } from "../core/format.js";
import { pagerMarkup, registerPager } from "../core/pager.js";
import { isCurrent, state } from "../core/state.js";
import { modalShell, showModal } from "../core/modal.js";
import { navActive, render, setTopbarTitle, toast } from "../core/ui.js";

let historyPage = 1;
const HISTORY_PAGE_SIZE = 20;

export async function pageHistory(token) {
  if (!isCurrent("history", token)) return;
  navActive("history"); setTopbarTitle("历史记录");
  let data, scripts, queues;
  try { [data, scripts, queues] = await Promise.all([api("GET", `/api/history?days=7&offset=${(historyPage - 1) * HISTORY_PAGE_SIZE}&limit=${HISTORY_PAGE_SIZE}`), api("GET", "/api/scripts"), api("GET", "/api/queues")]); }
  catch (error) { render(`<div class="empty"><strong>加载历史记录失败</strong>${esc(error.message)}</div>`); return; }
  if (!isCurrent("history", token)) return;
  const records = data.records || data;
  const total = data.total ?? records.length;
  const scriptName = id => scripts.find(script => script.id === id)?.name || "(已删除)";
  const queueName = id => queues.find(queue => queue.id === id)?.name || "";
  const content = records.length ? `<section class="card"><div class="table-scroll"><table class="data-table"><thead><tr><th>时间</th><th>脚本</th><th>队列</th><th>模式</th><th>结果</th><th>详情</th></tr></thead><tbody>${records.map(record => `<tr><td>${esc(fmtTime(record.startTime))}</td><td>${esc(scriptName(record.scriptInstanceId))}</td><td>${esc(queueName(record.queueId)) || "-"}</td><td>${record.mode === "auto" ? "自动" : "手动"}</td><td>${statusBadge(finalStatusOf(record))}</td><td class="ops">${esc(record.resultDetail)}<br><button class="sm" type="button" data-action="history-detail" data-id="${record.id}">查看详情</button></td></tr>`).join("")}</tbody></table></div>${pagerMarkup("history", historyPage, HISTORY_PAGE_SIZE, total)}</section>` : '<div class="empty"><strong>暂无历史记录</strong>运行脚本或调度队列后在此查看。</div>';
  render(`<div class="page-head"><div><div class="eyebrow">RUN ARCHIVE</div><h2>历史记录（最近 7 天，共 ${total} 条）</h2><p class="page-kicker">按运行时间查看结果、重试过程和脚本输出。</p></div></div>${content}`);
  registerPager("history", page => { historyPage = page; pageHistory(state.routeToken); });
}

export async function historyDetail(id) {
  try {
    const data = await api("GET", "/api/history/detail?id=" + encodeURIComponent(id));
    const record = data.record;
    if (!record) return;
    const attempts = (record.attemptDetails || []).map(attempt => `<div class="subsection"><h3>第 ${attempt.number} 次尝试：${attempt.status === "success" ? "成功" : attempt.status === "cancelled" ? "已取消" : "失败"}</h3><div class="detail"><div class="kv"><span class="k">原因</span><span>${esc(attempt.reason || "-")}</span></div><div class="kv"><span class="k">时间</span><span>${esc(fmtTime(attempt.startTime))} - ${esc(fmtTime(attempt.endTime))}</span></div></div>${attempt.outputTail ? `<div class="qk-row">控制台输出</div><pre class="logbox">${esc(attempt.outputTail.split("\n").slice(-12).join("\n"))}</pre>` : ""}</div>`).join("");
    const body = `<div class="detail"><div class="kv"><span class="k">开始</span><span>${esc(fmtTime(record.startTime))}</span></div><div class="kv"><span class="k">结束</span><span>${esc(fmtTime(record.endTime))}</span></div><div class="kv"><span class="k">模式</span><span>${record.mode === "auto" ? "自动运行" : "手动运行"}</span></div>${record.userName ? `<div class="kv"><span class="k">用户</span><span>${esc(record.userName)}</span></div>` : ""}<div class="kv"><span class="k">重试</span><span>${record.attemptCount || record.attempts} / ${record.maxAttempts || "-"} 次</span></div><div class="kv"><span class="k">结果</span><span>${statusBadge(finalStatusOf(record))} ${esc(record.resultDetail)}</span></div></div>${attempts}<div class="subsection"><h3>脚本日志（${data.logTotalLines || 0} 行${(data.logTotalLines || 0) > 200 ? "，仅显示尾部 200 行" : ""}）</h3><pre class="logbox">${esc(data.logTail || "（无脚本日志）")}</pre></div>`;
    showModal(modalShell(`${esc(record.scriptName)} 运行详情`, body, '<button class="ghost" type="button" data-action="close-modal">关闭</button>'), true);
  } catch (error) { toast(error.message, "error"); }
}

export const actions = {
  "history-detail": target => historyDetail(target.dataset.id),
};
