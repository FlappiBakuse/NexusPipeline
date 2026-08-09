import { api } from "../core/api.js";
import { $ as $dom } from "../core/dom.js";
import { esc } from "../core/format.js";
import { pageHeader, valueField } from "../core/forms.js";
import { pagerMarkup, registerPager } from "../core/pager.js";
import { isCurrent, state } from "../core/state.js";
import { closeModal, modalShell, showModal } from "../core/modal.js";
import { navActive, render, setTopbarTitle, toast } from "../core/ui.js";

let userModalScriptId = "";
let userEditingName = null;
let userDraft = null;
let userPage = 1;
const USER_PAGE_SIZE = 10;

export async function pageScriptUsers(scriptId, token) {
  const page = "scripts/" + scriptId + "/users";
  if (!isCurrent(page, token)) return;
  navActive("scripts");
  setTopbarTitle("用户管理");
  let scripts;
  try { scripts = await api("GET", "/api/scripts"); }
  catch (error) { render(`<div class="empty"><strong>加载用户失败</strong>${esc(error.message)}</div>`); return; }
  if (!isCurrent(page, token)) return;
  state.scripts = scripts;
  const script = scripts.find(item => item.id === scriptId);
  if (!script) { render('<div class="empty"><strong>脚本实例不存在</strong><a class="back-link" href="#/scripts">返回脚本实例</a></div>'); return; }
  const users = script.users || [];
  const atLimit = !!(state.limits && users.length >= state.limits.maxUsersPerScript);
  const action = `<button type="button" data-action="open-user-modal" data-id="${script.id}" ${atLimit ? "disabled" : ""}>添加用户${atLimit ? `（${users.length}/${state.limits.maxUsersPerScript}）` : ""}</button>`;
  const totalPages = Math.max(1, Math.ceil(users.length / USER_PAGE_SIZE));
  if (userPage > totalPages) userPage = totalPages;
  const pageItems = users.slice((userPage - 1) * USER_PAGE_SIZE, userPage * USER_PAGE_SIZE);
  const usersMarkup = users.length ? `<section class="card">${pageItems.map((user, pageIndex) => {
    const userIndex = (userPage - 1) * USER_PAGE_SIZE + pageIndex;
    const first = userIndex === 0;
    const last = userIndex === users.length - 1;
    return `<article class="user-card">
    <div class="list-item-head"><div><div class="list-item-title"><strong>${esc(user.name)}</strong>${user.enabled ? '<span class="badge ok">已启用</span>' : '<span class="badge muted">已禁用</span>'}</div>
      <div class="qk-row">任务前脚本：${user.preRunScript ? `<span class="mono">${esc(user.preRunScript)}</span>${user.preRunOnceOnly ? "（仅首次）" : ""}` : '<span class="muted">未设置</span>'}</div>
      <div class="qk-row">任务后脚本：${user.postRunScript ? `<span class="mono">${esc(user.postRunScript)}</span>${user.postRunOnFinalOnly ? "（仅最终完成）" : ""}` : '<span class="muted">未设置</span>'}</div>
    </div>
      <div class="action-row"><div class="user-actions">
        <button class="sm" type="button" data-action="edit-user-config" data-id="${script.id}" data-name="${esc(user.name)}">编辑配置</button>
        <button class="sm" type="button" data-action="edit-user" data-id="${script.id}" data-name="${esc(user.name)}">编辑用户</button>
        <button class="sm danger" type="button" data-action="delete-user" data-id="${script.id}" data-name="${esc(user.name)}">删除用户</button>
        <span class="user-actions-spacer" aria-hidden="true"></span>
        <button class="sm" type="button" data-action="move-user-up" data-id="${script.id}" data-name="${esc(user.name)}" ${first ? "disabled" : ""} title="${first ? "已是第一位用户" : "上移用户"}">上移用户</button>
        <button class="sm" type="button" data-action="move-user-down" data-id="${script.id}" data-name="${esc(user.name)}" ${last ? "disabled" : ""} title="${last ? "已是最后一位用户" : "下移用户"}">下移用户</button>
      </div></div>
    </div>
  </article>`;
  }).join("")}${users.length > USER_PAGE_SIZE ? pagerMarkup("users", userPage, USER_PAGE_SIZE, users.length) : ""}</section>` : '<div class="empty"><strong>暂无用户</strong>点击右上角「添加用户」创建。</div>';
  render(pageHeader("SCRIPT USERS", `${esc(script.name)} · 用户管理`, "为不同用户保存独立配置，运行时会自动交换并还原。", action) + `<div class="back-row"><a class="back-link" href="#/scripts">← 返回脚本实例</a></div>${usersMarkup}`);
  registerPager("users", p => { userPage = p; pageScriptUsers(scriptId, state.routeToken); });
}

export function openUserModal(scriptId, userName = "") {
  const script = state.scripts.find(item => item.id === scriptId);
  if (!script) { toast("脚本不存在", "error"); return; }
  const user = userName ? (script.users || []).find(item => item.name === userName) : null;
  userModalScriptId = scriptId;
  userEditingName = userName || null;
  userDraft = {
    name: user?.name || "", enabled: user?.enabled !== false, preRunScript: user?.preRunScript || "", preRunOnceOnly: !!user?.preRunOnceOnly,
    postRunScript: user?.postRunScript || "", postRunOnFinalOnly: !!user?.postRunOnFinalOnly,
  };
  const d = userDraft;
  const body = `${valueField("um-name", "用户名 <span class='req'>*</span>", d.name, "text", 'placeholder="脚本内不可重复"')}
    <label class="check"><input id="um-enabled" type="checkbox" ${d.enabled ? "checked" : ""}><span>启用该用户（禁用后不可选用于运行）</span></label>
    <div class="subsection"><h3>任务前运行脚本</h3>${valueField("um-pre", "脚本路径（填写地址则启用，留空不启用）", d.preRunScript)}<label class="check"><input id="um-pre-once" type="checkbox" ${d.preRunOnceOnly ? "checked" : ""}><span>仅首次运行启用（重试时不再执行）</span></label></div>
    <div class="subsection"><h3>任务后运行脚本</h3>${valueField("um-post", "脚本路径（填写地址则启用，留空不启用）", d.postRunScript)}<label class="check"><input id="um-post-final" type="checkbox" ${d.postRunOnFinalOnly ? "checked" : ""}><span>仅最终运行完成启用</span></label></div>`;
  showModal(modalShell(user ? "编辑用户" : "添加用户", body, '<button type="button" data-action="save-user">保存</button><button class="ghost" type="button" data-action="close-modal">取消</button>'));
}

export async function saveUser() {
  const name = $dom("#um-name")?.value.trim();
  if (!name) { toast("请填写用户名", "error"); $dom("#um-name")?.focus(); return; }
  const payload = { name, enabled: $dom("#um-enabled").checked, preRunScript: $dom("#um-pre").value.trim(), preRunOnceOnly: $dom("#um-pre-once").checked, postRunScript: $dom("#um-post").value.trim(), postRunOnFinalOnly: $dom("#um-post-final").checked };
  try {
    const base = "/api/scripts/" + userModalScriptId + "/users";
    if (userEditingName) await api("PUT", base + "/" + encodeURIComponent(userEditingName), payload);
    else await api("POST", base, payload);
    const id = userModalScriptId;
    closeModal(); toast("用户已保存");
    await pageScriptUsers(id, state.routeToken);
  } catch (error) { toast(error.message, "error"); }
}

export async function deleteUser(scriptId, userName) {
  if (!confirm("确定删除用户「" + userName + "」？（该用户保存的配置会一并删除）")) return;
  try { await api("DELETE", `/api/scripts/${scriptId}/users/${encodeURIComponent(userName)}`); toast("用户已删除"); await pageScriptUsers(scriptId, state.routeToken); }
  catch (error) { toast(error.message, "error"); }
}

export async function moveUser(scriptId, userName, direction) {
  const script = state.scripts.find(item => item.id === scriptId);
  if (!script) return;
  const users = script.users || [];
  const index = users.findIndex(user => user.name === userName);
  const other = index + direction;
  if (index < 0 || other < 0 || other >= users.length) return;
  const names = users.map(user => user.name);
  [names[index], names[other]] = [names[other], names[index]];
  try {
    await api("PUT", `/api/scripts/${scriptId}/users/order`, { names });
    toast(direction < 0 ? "用户已上移" : "用户已下移");
    await pageScriptUsers(scriptId, state.routeToken);
  } catch (error) { toast(error.message, "error"); }
}

export async function editUserConfig(scriptId, userName) {
  try {
    await api("POST", `/api/scripts/${scriptId}/users/${encodeURIComponent(userName)}/edit-config`, { action: "start" });
    showModal(modalShell("配置编辑中", `<p class="modal-copy">主程序已启动（不带参数）。请自行设置好当前用户「${esc(userName)}」的脚本配置。设置完成后点击「完成」保存，或点击「取消」放弃本次修改。</p>`, `<button type="button" data-action="edit-config-done" data-id="${scriptId}" data-name="${esc(userName)}">完成</button><button class="ghost" type="button" data-action="edit-config-cancel" data-id="${scriptId}" data-name="${esc(userName)}">取消</button>`));
  } catch (error) { toast(error.message, "error"); }
}

export async function editConfigAction(scriptId, userName, action) {
  try { await api("POST", `/api/scripts/${scriptId}/users/${encodeURIComponent(userName)}/edit-config`, { action }); closeModal(); toast(action === "done" ? "已保存当前用户配置" : "已取消，配置已还原"); await pageScriptUsers(scriptId, state.routeToken); }
  catch (error) { toast(error.message, "error"); }
}

export const actions = {
  "manage-users": target => { location.hash = "#/scripts/" + target.dataset.id + "/users"; },
  "open-user-modal": target => openUserModal(target.dataset.id),
  "edit-user": target => openUserModal(target.dataset.id, target.dataset.name),
  "save-user": () => saveUser(),
  "delete-user": target => deleteUser(target.dataset.id, target.dataset.name),
  "move-user-up": target => moveUser(target.dataset.id, target.dataset.name, -1),
  "move-user-down": target => moveUser(target.dataset.id, target.dataset.name, 1),
  "edit-user-config": target => editUserConfig(target.dataset.id, target.dataset.name),
  "edit-config-done": target => editConfigAction(target.dataset.id, target.dataset.name, "done"),
  "edit-config-cancel": target => editConfigAction(target.dataset.id, target.dataset.name, "cancel"),
};
