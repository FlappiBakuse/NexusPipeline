import { test, expect } from "@playwright/test";
import { api, baseUrl, createScript, makeScriptDir, PING_GAME } from "./helpers.mjs";

test("脚本入口：创建、编辑和删除一个普通脚本", async ({ page }) => {
  const fixture = makeScriptDir("smoke-script-crud");
  let createdId = "";
  try {
    await page.goto(baseUrl + "#/scripts", { waitUntil: "domcontentloaded" });
    await page.getByTestId("new-script").click();
    let modal = page.locator(".modal");
    await expect(modal).toBeVisible();
    const genericChooser = modal.getByRole("button", { name: /新建通用脚本实例/ });
    if (await genericChooser.count()) {
      await genericChooser.click();
      modal = page.locator(".modal");
      await expect(modal.locator("#sm-mode-btn")).toBeVisible();
    }
    const judgeMode = modal.locator("#sm-mode-btn");
    await judgeMode.click();
    const judgeUpload = modal.locator("#sm-upload-btn");
    await expect(judgeUpload).toBeVisible();
    await judgeMode.click();
    await modal.locator("#sm-name").fill("Smoke 普通脚本");
    await modal.locator("#sm-root").fill(fixture.root);
    await modal.locator("#sm-exe").fill(fixture.main);
    await modal.locator("#sm-config").fill(fixture.cfg);
    await modal.locator("#sm-log").fill(fixture.log);
    await modal.locator("#sm-game-exe").fill(PING_GAME);
    await modal.getByRole("button", { name: "保存", exact: true }).click();
    await expect(modal).toBeHidden();
    const created = (await (await api("GET", "/api/scripts")).json()).find(item => item.name === "Smoke 普通脚本");
    createdId = created?.id || "";
    await expect(page.getByTestId("script-card").filter({ hasText: "Smoke 普通脚本" })).toBeVisible();

    const card = page.getByTestId("script-card").filter({ hasText: "Smoke 普通脚本" }).first();
    await card.getByRole("button", { name: "编辑脚本", exact: true }).click();
    await expect(page.locator("#sm-name")).toHaveValue("Smoke 普通脚本");
    await page.locator("#sm-name").fill("Smoke 普通脚本-已编辑");
    await page.locator(".modal").getByRole("button", { name: "保存", exact: true }).click();
    await expect(page.getByTestId("script-card").filter({ hasText: "Smoke 普通脚本-已编辑" })).toBeVisible();

    await page.getByTestId("script-card").filter({ hasText: "Smoke 普通脚本-已编辑" }).first().locator('[data-action="delete-script"]').click();
    await page.locator('[data-action="confirm-delete-script"]').click();
    await expect(page.getByTestId("script-card").filter({ hasText: "Smoke 普通脚本-已编辑" })).toHaveCount(0);
    createdId = "";
  } finally {
    if (createdId) await api("DELETE", `/api/scripts/${encodeURIComponent(createdId)}`);
  }
});

test("表单校验：脚本实例与调度队列标记空必填项", async ({ page }) => {
  await page.goto(baseUrl + "#/scripts", { waitUntil: "domcontentloaded" });
  await page.getByTestId("new-script").click();
  let modal = page.locator(".modal");
  await expect(modal).toBeVisible();
  const genericChooser = modal.getByRole("button", { name: /新建通用脚本实例/ });
  if (await genericChooser.count()) await genericChooser.click();
  modal = page.locator(".modal").last();
  await modal.getByRole("button", { name: "保存", exact: true }).click();
  await expect(modal.locator("#sm-name")).toHaveClass(/field-error/);
  await expect(modal.locator("#sm-root")).toHaveClass(/field-error/);
  await expect(modal.locator("#sm-name")).toHaveAttribute("aria-invalid", "true");
  await expect(modal.locator("#sm-name")).toBeFocused();
  await modal.locator("#sm-name").fill("Smoke 校验脚本");
  await expect(modal.locator("#sm-name")).not.toHaveClass(/field-error/);
  await modal.locator(".modal-close").last().click();

  await page.goto(baseUrl + "#/queues", { waitUntil: "domcontentloaded" });
  await page.getByRole("button", { name: "新建调度队列", exact: true }).click();
  modal = page.locator(".modal").last();
  await modal.getByRole("button", { name: "保存", exact: true }).click();
  await expect(modal.locator("#qm-name")).toHaveClass(/field-error/);
  await expect(modal.locator("#qm-name")).toHaveAttribute("aria-invalid", "true");
  await modal.locator("#qm-name").fill("Smoke 校验队列");
  await expect(modal.locator("#qm-name")).not.toHaveClass(/field-error/);
  await modal.locator(".modal-close").click();
});

test("用户管理：修改插件字段并保存用户绑定", async ({ page }) => {
  const suffix = Date.now();
  const fixture = makeScriptDir(`smoke-user-binding-${suffix}`);
  let badgeUserId = "";
  let pluginEnabled = true;
  await page.route("**/api/plugin-contributions/user-list-badges", async route => {
    if (route.request().method() !== "GET") {
      await route.continue();
      return;
    }
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify([{
        userId: badgeUserId,
        badges: [{
          pluginName: "game-checkin",
          pluginDisplayName: "游戏自动签到",
          id: "check-in-status",
          label: "签到 · 今日完成",
          tone: "ok",
          title: "今日签到已经完成",
          order: 100,
        }],
      }]),
    });
  });
  await page.route("**/api/plugin-contributions/user-global/**", async route => {
    const request = route.request();
    if (request.method() === "GET") {
      await route.fulfill({
        status: 200,
        contentType: "application/json",
        body: JSON.stringify([{
          pluginName: "game-checkin",
          pluginDisplayName: "游戏自动签到",
          id: "user-settings",
          title: "游戏自动签到",
          description: "按需管理自动签到。",
          fields: [
            { key: "enabled", label: "启用自动签到", type: "switch", description: "关闭后保留配置但不执行签到。", required: true },
          ],
          values: {
            enabled: pluginEnabled,
          },
        }]),
      });
      return;
    }
    if (request.method() === "PUT") {
      pluginEnabled = request.postDataJSON()?.values?.enabled ?? pluginEnabled;
      await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ ok: true }) });
      return;
    }
    await route.continue();
  });
  const scriptResponse = await api("POST", "/api/scripts", {
    name: `Smoke 绑定脚本-${suffix}`,
    rootPath: fixture.root,
    mainExe: fixture.main,
    configPath: fixture.cfg,
    logPath: fixture.log,
    gameExe: PING_GAME,
    maxAttempts: 1,
    logStallTimeoutMinutes: 5,
    totalTimeoutMinutes: 120,
  });
  const script = await scriptResponse.json();
  const userResponse = await api("POST", "/api/users", { name: `Smoke 绑定用户-${suffix}` });
  const user = await userResponse.json();
  badgeUserId = user.id;
  await api("POST", `/api/users/${encodeURIComponent(user.id)}/bindings`, {
    scriptInstanceId: script.id,
    enabled: true,
    notifyEnabled: true,
    smtpTo: "old@example.com",
  });

  try {
    await page.goto(baseUrl + "#/users", { waitUntil: "domcontentloaded" });
    const card = page.getByTestId("global-user-card").filter({ hasText: user.name }).first();
    const badge = card.getByTestId("plugin-user-badge").first();
    await expect(badge).toHaveText("签到 · 今日完成");
    await card.getByRole("button", { name: "全局管理", exact: true }).click();
    const globalDialog = page.getByRole("dialog", { name: "全局管理" });
    await expect(globalDialog).toBeVisible();
    await expect(globalDialog.getByRole("heading", { name: "通用", exact: true })).toBeVisible();
    const plugin = globalDialog.locator(".global-management-plugin");
    const pluginSwitch = plugin.getByRole("button", { name: "启用自动签到", exact: true });
    await expect(pluginSwitch).toHaveAttribute("aria-pressed", "true");
    await pluginSwitch.click();
    await expect(pluginSwitch).toHaveAttribute("aria-pressed", "false");
    await globalDialog.getByRole("button", { name: "保存", exact: true }).click();
    await expect(globalDialog).toBeHidden();
    await card.getByRole("button", { name: "全局管理", exact: true }).click();
    const reopenedGlobalDialog = page.getByRole("dialog", { name: "全局管理" });
    await expect(reopenedGlobalDialog.getByRole("button", { name: "启用自动签到", exact: true })).toHaveAttribute("aria-pressed", "false");
    await reopenedGlobalDialog.getByRole("button", { name: "取消", exact: true }).click();

    await card.getByRole("button", { name: "用户管理", exact: true }).click();
    const dialog = page.getByRole("dialog", { name: "用户管理" });
    await expect(dialog.getByTestId("um-binding-card")).toHaveCount(1);
    const binding = dialog.getByTestId("um-binding-card").first();
    await binding.locator('[data-action="toggle-um-binding"]').click();
    await expect(binding.getByRole("heading", { name: "通用", exact: true })).toBeVisible();
    await expect(binding.getByRole("heading", { name: "通知", exact: true })).toBeVisible();
    await expect(binding.getByRole("heading", { name: "高级", exact: true })).toBeVisible();
    await dialog.getByLabel("SMTP 收件人", { exact: true }).fill("new@example.com");
    await dialog.getByLabel("最多成功运行次数", { exact: true }).fill("2");
    await dialog.getByRole("button", { name: "保存", exact: true }).click();
    await expect(dialog).toBeHidden();

    const updated = await (await api("GET", `/api/users/${encodeURIComponent(user.id)}`)).json();
    const updatedBinding = updated.bindings.find(item => item.scriptInstanceId === script.id);
    expect(updatedBinding.smtpTo).toBe("new@example.com");
    expect(updatedBinding.maxSuccessfulRunsPerDay).toBe(2);

    const deleteScriptResponse = await api("DELETE", `/api/scripts/${encodeURIComponent(script.id)}`);
    expect(deleteScriptResponse.ok).toBeTruthy();
    const afterScriptDelete = await (await api("GET", `/api/users/${encodeURIComponent(user.id)}`)).json();
    expect(afterScriptDelete.bindings.some(item => item.scriptInstanceId === script.id)).toBeFalsy();

    await page.reload({ waitUntil: "domcontentloaded" });
    const refreshedCard = page.getByTestId("global-user-card").filter({ hasText: user.name }).first();
    await refreshedCard.getByRole("button", { name: "用户管理", exact: true }).click();
    const refreshedDialog = page.getByRole("dialog", { name: "用户管理" });
    await expect(refreshedDialog.getByTestId("um-binding-card")).toHaveCount(0);
    await expect(refreshedDialog).not.toContainText("（脚本实例不存在）");
    await refreshedDialog.locator('[data-action="close-modal"]').click();
  } finally {
    await api("DELETE", `/api/users/${encodeURIComponent(user.id)}`, { confirmName: user.name });
    await api("DELETE", `/api/scripts/${encodeURIComponent(script.id)}`);
  }
});
