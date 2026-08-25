import fs from "node:fs";
import path from "node:path";
import { test, expect } from "@playwright/test";
import { api, baseUrl, ensureService, makeScriptDir, PING_GAME, runtimeDir } from "./helpers.mjs";

await ensureService();

function userDataDir(scriptId, userId) {
  return path.join(runtimeDir, "data", scriptId, userId);
}

async function responseJson(response) {
  return response.ok ? response.json() : null;
}

test("全局用户 API：稳定 UserId、绑定、头像校验与精确删除确认", async () => {
  const suffix = String(Date.now());
  const name = `v096-api-${suffix}`;
  const renamed = `${name}-renamed`;
  const scriptDir = makeScriptDir(`global-users-${suffix}`);
  let scriptId = "";
  let userId = "";
  let deleted = false;

  try {
    const scriptResponse = await api("POST", "/api/scripts", {
      name: `全局用户回归-${suffix}`,
      rootPath: scriptDir.root,
      mainExe: scriptDir.main,
      configPath: scriptDir.cfg,
      logPath: scriptDir.log,
      gameExe: PING_GAME,
      maxAttempts: 1,
      logStallTimeoutMinutes: 5,
      totalTimeoutMinutes: 120,
    });
    const scriptError = scriptResponse.ok ? "" : await scriptResponse.text();
    expect(scriptResponse.ok, `创建回归脚本（${scriptError}）`).toBeTruthy();
    scriptId = (await scriptResponse.json()).id;

    const createResponse = await api("POST", "/api/users", { name, autoCheckInEnabled: false });
    expect(createResponse.ok, "创建全局用户").toBeTruthy();
    const created = await createResponse.json();
    userId = created.id;
    expect(typeof userId === "string" && userId.length > 20, "全局用户返回稳定 UserId").toBeTruthy();

    const duplicate = await api("POST", "/api/users", { name: name.toUpperCase(), autoCheckInEnabled: false });
    expect(duplicate.status === 400, "全局用户名大小写不敏感去重").toBeTruthy();

    const binding = await api("POST", `/api/users/${encodeURIComponent(userId)}/bindings`, {
      scriptInstanceId: scriptId,
      enabled: true,
      notifyEnabled: true,
      runDays: 3,
    });
    expect(binding.ok, "创建全局用户脚本绑定").toBeTruthy();
    const bound = await binding.json();
    expect(bound.runDays === 3, "绑定返回运行天数").toBeTruthy();
    expect(bound.runDays !== 0 && bound.scriptInstanceId === scriptId, "绑定返回脚本实例").toBeTruthy();
    const idDirectory = userDataDir(scriptId, userId);
    expect(fs.existsSync(idDirectory), "绑定配置落在 UserId 目录").toBeTruthy();

    const negativeDays = await api("PUT", `/api/users/${encodeURIComponent(userId)}/bindings/${encodeURIComponent(scriptId)}`, {
      scriptInstanceId: scriptId,
      enabled: true,
      notifyEnabled: true,
      runDays: -2,
    });
    expect(negativeDays.status === 400, "运行天数小于 -1 被拒绝").toBeTruthy();

    const png = Buffer.from("89504e470d0a1a0a", "hex");
    const avatarResponse = await api("POST", `/api/users/${encodeURIComponent(userId)}/avatar`, {
      mimeType: "image/png",
      data: png.toString("base64"),
    });
    expect(avatarResponse.ok, "头像上传").toBeTruthy();
    const avatarGet = await fetch(`${baseUrl}api/users/${encodeURIComponent(userId)}/avatar`);
    expect(avatarGet.ok && avatarGet.headers.get("x-content-type-options") === "nosniff", "头像返回安全响应头").toBeTruthy();

    const renameResponse = await api("PUT", `/api/users/${encodeURIComponent(userId)}`, {
      name: renamed,
      remark: "备注内容一",
      autoCheckInEnabled: false,
    });
    const renamedUser = await responseJson(renameResponse);
    expect(renameResponse.ok && renamedUser?.id === userId && renamedUser.name === renamed && renamedUser.remark === "备注内容一", "改名与备注保存").toBeTruthy();
    expect(fs.existsSync(idDirectory), "改名不移动 UserId 配置目录").toBeTruthy();

    const wrongConfirmation = await api("DELETE", `/api/users/${encodeURIComponent(userId)}`, { confirmName: name });
    expect(wrongConfirmation.status === 400, "删除必须精确输入当前用户名").toBeTruthy();
    const deleteResponse = await api("DELETE", `/api/users/${encodeURIComponent(userId)}`, { confirmName: renamed });
    expect(deleteResponse.ok, "精确确认后删除全局用户").toBeTruthy();
    deleted = true;
    expect(!fs.existsSync(idDirectory), "删除用户清理 UserId 配置目录").toBeTruthy();
    expect((await fetch(`${baseUrl}api/users/${encodeURIComponent(userId)}/avatar`)).status === 404, "删除用户清理头像").toBeTruthy();
  } finally {
    if (userId && !deleted) {
      await api("DELETE", `/api/users/${encodeURIComponent(userId)}`, { confirmName: renamed });
    }
    if (scriptId) {
      await api("DELETE", `/api/scripts/${encodeURIComponent(scriptId)}`);
    }
  }
});

test("全局用户页面：创建、展示与完整用户名删除确认", async ({ page }) => {
  const name = `v096-ui-${Date.now()}`;
  let userId = "";
  try {
    await page.goto(`${baseUrl}#/users`, { waitUntil: "domcontentloaded" });
    await page.waitForSelector('[data-action="open-global-user-modal"]', { timeout: 10000 });
    await page.click('[data-action="open-global-user-modal"]');
    await page.fill("#gu-name", name);
    await page.click('[data-action="save-global-user"]');
    await page.waitForFunction(expected => Array.from(document.querySelectorAll("[data-testid='global-user-card']"))
      .some(card => card.textContent.includes(expected)), name, { timeout: 10000 });

    const card = page.locator("[data-testid='global-user-card']").filter({ hasText: name }).first();
    await card.locator('[data-action="delete-global-user"]').click();
    await page.fill("#gu-delete-name", name);
    await page.click('[data-action="confirm-delete-global-user"]');
    await page.waitForFunction(expected => !Array.from(document.querySelectorAll("[data-testid='global-user-card']"))
      .some(card => card.textContent.includes(expected)), name, { timeout: 10000 });
  } finally {
    const users = await (await api("GET", "/api/users")).json();
    const user = users.find(item => item.name === name);
    userId = user?.id || "";
    if (userId) {
      await api("DELETE", `/api/users/${encodeURIComponent(userId)}`, { confirmName: name });
    }
  }
});

test("全局用户页面：脚本卡片结构、统一绑定管理与响应式操作栏", async ({ page }) => {
  const suffix = String(Date.now());
  const userName = `v096-layout-${suffix}`;
  const scriptName = `用户管理脚本-${suffix}`;
  const queueName = `用户管理队列-${suffix}`;
  const scriptDir = makeScriptDir(`global-users-layout-${suffix}`);
  let scriptId = "";
  let queueId = "";
  let userId = "";

  try {
    const scriptResponse = await api("POST", "/api/scripts", {
      name: scriptName,
      rootPath: scriptDir.root,
      mainExe: scriptDir.main,
      configPath: scriptDir.cfg,
      logPath: scriptDir.log,
      gameExe: PING_GAME,
      maxAttempts: 1,
      logStallTimeoutMinutes: 5,
      totalTimeoutMinutes: 120,
    });
    expect(scriptResponse.ok, "创建布局回归脚本").toBeTruthy();
    scriptId = (await scriptResponse.json()).id;

    const queueResponse = await api("POST", "/api/queues", {
      name: queueName,
      autoRunMode: "none",
      completionAction: "none",
      timeSets: [],
      tasks: [{ id: "", index: 0, scriptInstanceId: scriptId }],
    });
    expect(queueResponse.ok, "创建布局回归队列").toBeTruthy();
    queueId = (await queueResponse.json()).id;

    const userResponse = await api("POST", "/api/users", { name: userName, autoCheckInEnabled: false });
    expect(userResponse.ok, "创建布局回归用户").toBeTruthy();
    userId = (await userResponse.json()).id;
    const bindingResponse = await api(`POST`, `/api/users/${encodeURIComponent(userId)}/bindings`, {
      scriptInstanceId: scriptId,
      enabled: true,
      notifyEnabled: true,
      preRunScript: "before.cmd",
      preRunOnceOnly: true,
      postRunScript: "after.cmd",
      postRunOnFinalOnly: true,
      smtpTo: "layout@example.com",
    });
    expect(bindingResponse.ok, "创建布局回归用户绑定").toBeTruthy();

    await page.goto(`${baseUrl}#/users`, { waitUntil: "domcontentloaded" });
    const card = page.locator("[data-testid='global-user-card']").filter({ hasText: userName }).first();
    await expect(card).toBeVisible();
    await expect(card.locator(".drag-handle")).toHaveAttribute("aria-label", "拖拽调整全局用户顺序");
    await expect(card.locator(".global-user-avatar-button")).toHaveAttribute("aria-label", new RegExp("点击上传或更换"));
    await expect(card.locator(".global-user-bindings")).toHaveCount(0);
    await expect(card.getByRole("button", { name: "用户管理", exact: true })).toBeVisible();
    await expect(card.getByRole("button", { name: "删除用户", exact: true })).toBeVisible();
    await expect(card.getByText("自动签到未开启 · 即将开发", { exact: true })).toBeVisible();

    await card.getByRole("button", { name: "用户管理", exact: true }).click();
    const dialog = page.getByRole("dialog", { name: "用户管理" });
    await expect(dialog).toBeVisible();
    await expect(dialog.getByRole("heading", { name: "已绑定脚本实例", exact: true })).toBeVisible();
    await expect(dialog.getByRole("textbox", { name: "用户名", exact: false })).toHaveValue(userName);
    await expect(dialog.getByRole("textbox", { name: "备注", exact: true })).toBeVisible();
    await expect(dialog.getByRole("button", { name: "添加脚本", exact: true })).toBeVisible();
    await expect(dialog.getByRole("button", { name: "自动签到选项", exact: true })).toHaveCount(0);
    await expect(dialog.locator("[data-testid='um-binding-card']")).toHaveCount(1);

    // 展开绑定卡片：参与运行开关与移除绑定出现在头部，其他入口隐藏。
    const bindingCard = dialog.locator("[data-testid='um-binding-card']").first();
    await bindingCard.locator('[data-action="toggle-um-binding"]').click();
    await expect(dialog.getByRole("button", { name: "参与运行", exact: true })).toBeVisible();
    await expect(dialog.getByRole("button", { name: "移除绑定", exact: true })).toBeVisible();
    await expect(dialog.locator('[data-action="edit-user-config-global"]')).toBeVisible();
    await expect(dialog.locator(".um-option-card.is-placeholder")).toContainText("自动签到选项");
    await expect(dialog.locator(".um-option-card.is-placeholder")).toContainText("即将开发");

    // 通知推送二级页：开关 + SMTP 收件人。
    await bindingCard.locator('[data-action="set-um-subview"][data-view="notify"]').click();
    await expect(dialog.getByRole("button", { name: "开启通知推送", exact: true })).toBeVisible();
    await expect(dialog.getByRole("textbox", { name: "SMTP 收件人", exact: true })).toHaveValue("layout@example.com");
    await expect(dialog.getByText("仅 SMTP 使用；留空继承全局收件人，Webhook 不受影响。", { exact: true })).toBeVisible();
    await bindingCard.getByRole("button", { name: "返回上级", exact: true }).click();

    // 高级选项二级页：前后置路径与运行天数。
    await bindingCard.locator('[data-action="set-um-subview"][data-view="advanced"]').click();
    await expect(dialog.getByRole("textbox", { name: "任务前运行脚本路径", exact: true })).toHaveValue("%FIRST% before.cmd");
    await expect(dialog.getByRole("textbox", { name: "任务后运行脚本路径", exact: true })).toHaveValue("%LAST% after.cmd");
    await expect(dialog.getByRole("spinbutton", { name: "运行天数", exact: true })).toHaveValue("-1");
    await bindingCard.getByRole("button", { name: "返回上级", exact: true }).click();

    // 收回：恢复 1/2 网格布局。
    await bindingCard.locator('[data-action="toggle-um-binding"]').click();
    await expect(bindingCard).not.toHaveClass(/is-expanded/);
    await dialog.getByRole("button", { name: "取消", exact: true }).click();

    await page.goto(`${baseUrl}#/scripts`, { waitUntil: "domcontentloaded" });
    const scriptCard = page.locator("[data-testid='script-card']").filter({ hasText: scriptName }).first();
    await expect(scriptCard.locator(".script-ops").getByRole("button", { name: "编辑脚本", exact: true })).toHaveCount(1);
    await expect(scriptCard.locator(".script-ops").getByRole("button", { name: "删除脚本", exact: true })).toHaveCount(1);
    await expect(scriptCard.locator('[data-action="manage-users"], [data-action="open-user-management"], .overflow-trigger')).toHaveCount(0);

    await page.goto(`${baseUrl}#/queues`, { waitUntil: "domcontentloaded" });
    const queueCard = page.locator("[data-testid='queue-card']").filter({ hasText: queueName }).first();
    await expect(queueCard.locator(".queue-ops").getByRole("button", { name: "编辑队列", exact: true })).toHaveCount(1);
    await expect(queueCard.locator(".queue-ops").getByRole("button", { name: "删除队列", exact: true })).toHaveCount(1);
    await expect(queueCard.locator('.overflow-trigger')).toHaveCount(0);

    await page.goto(`${baseUrl}#/users`, { waitUntil: "domcontentloaded" });
    for (const width of [360, 768, 1280]) {
      await page.setViewportSize({ width, height: 900 });
      await page.reload({ waitUntil: "domcontentloaded" });
      await expect(page.locator("[data-testid='global-user-card']").filter({ hasText: userName }).first()).toBeVisible();
      const layout = await page.evaluate(() => ({
        viewport: document.documentElement.clientWidth,
        content: document.documentElement.scrollWidth,
        card: document.querySelector("[data-testid='global-user-card']")?.getBoundingClientRect().width || 0,
      }));
      expect(layout.content, `${width}px 页面不应出现横向溢出`).toBeLessThanOrEqual(layout.viewport + 1);
      expect(layout.card, `${width}px 用户卡片不应超出视口`).toBeLessThanOrEqual(layout.viewport + 1);
    }
  } finally {
    if (userId) await api("DELETE", `/api/users/${encodeURIComponent(userId)}`, { confirmName: userName });
    if (queueId) await api("DELETE", `/api/queues/${encodeURIComponent(queueId)}`);
    if (scriptId) await api("DELETE", `/api/scripts/${encodeURIComponent(scriptId)}`);
    fs.rmSync(scriptDir.root, { recursive: true, force: true });
  }
});
