import { test, expect } from "@playwright/test";
import { api, baseUrl, createScript, makeScriptDir, PING_GAME } from "./helpers.mjs";

test("脚本入口：创建、编辑和删除一个普通脚本", async ({ page }) => {
  const fixture = makeScriptDir("smoke-script-crud");
  let createdId = "";
  try {
    await page.goto(baseUrl + "#/scripts", { waitUntil: "domcontentloaded" });
    await page.getByTestId("new-script").click();
    await page.locator('.new-script-chooser [data-action="open-script-type"][data-plugin=""]').click();
    const modal = page.locator(".modal");
    await expect(modal).toBeVisible();
    await modal.locator("#sm-name").fill("Smoke 普通脚本");
    await modal.locator("#sm-root").fill(fixture.root);
    await modal.locator("#sm-exe").fill(fixture.main);
    await modal.locator("#sm-config").fill(fixture.cfg);
    await modal.locator("#sm-log").fill(fixture.log);
    await modal.locator("#sm-game-exe").fill(PING_GAME);
    await modal.getByRole("button", { name: "保存", exact: true }).click();
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

test("全局用户入口：创建并用完整用户名确认删除", async ({ page }) => {
  const name = `Smoke 用户-${Date.now()}`;
  await page.goto(baseUrl + "#/users", { waitUntil: "domcontentloaded" });
  await page.getByTestId("open-global-user-modal").click();
  await page.locator("#gu-name").fill(name);
  await page.getByTestId("save-global-user").click();
  const card = page.getByTestId("global-user-card").filter({ hasText: name }).first();
  await expect(card).toBeVisible();
  await card.locator('[data-action="delete-global-user"]').click();
  await page.locator("#gu-delete-name").fill(name);
  await page.getByTestId("confirm-delete-global-user").click();
  await expect(page.getByTestId("global-user-card").filter({ hasText: name })).toHaveCount(0);
});

test("用户绑定入口：打开管理、修改通知收件人并保存", async ({ page }) => {
  const suffix = Date.now();
  const fixture = makeScriptDir(`smoke-user-binding-${suffix}`);
  let badgeUserId = "";
  let savedPluginPayload = null;
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
          pluginName: "hoyolab-checkin",
          pluginDisplayName: "HoYoLAB 自动签到",
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
          pluginName: "hoyolab-checkin",
          pluginDisplayName: "HoYoLAB 自动签到",
          id: "user-settings",
          title: "HoYoLAB 自动签到",
          description: "管理用户的签到开关、Cookie 和目标游戏。",
          fields: [
            { key: "enabled", label: "启用自动签到", type: "switch", description: "关闭后保留配置但不执行签到。", required: true },
            { key: "cookie", label: "HoYoLAB Cookie", type: "secret", description: "完整 Cookie 将由宿主加密保存。", placeholder: "请输入完整 Cookie", maxLength: 16384 },
            {
              key: "games",
              label: "签到游戏",
              type: "multi-select",
              description: "选择需要签到的游戏。",
              required: true,
              options: [
                { value: "gi", label: "原神" },
                { value: "hsr", label: "崩坏：星穹铁道" },
                { value: "zzz", label: "绝区零" },
              ],
            },
            { key: "lastStatus", label: "最近状态", type: "status", readOnly: true },
          ],
          values: { enabled: true, cookie: { configured: false }, games: ["gi"], lastStatus: "尚未尝试" },
        }]),
      });
      return;
    }
    if (request.method() === "PUT") {
      savedPluginPayload = JSON.parse(request.postData() || "{}");
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
    await expect(badge).toHaveAttribute("data-plugin-name", "hoyolab-checkin");
    await expect(badge).toHaveAttribute("data-contribution-id", "check-in-status");
    await card.getByRole("button", { name: "全局管理", exact: true }).click();
    const globalDialog = page.getByRole("dialog", { name: "全局管理" });
    await expect(globalDialog).toBeVisible();
    await expect(globalDialog.getByRole("heading", { name: "通用", exact: true })).toBeVisible();
    const plugin = globalDialog.locator(".global-management-plugin");
    await expect(plugin).not.toContainText("aria-pressed=");
    await expect(plugin.getByRole("button", { name: "启用自动签到" })).toHaveAttribute("aria-pressed", "true");
    await expect(plugin.locator(":scope > .section-heading > div > strong")).toHaveCount(0);
    const gameTrigger = plugin.locator(".plugin-multi-select-trigger");
    await expect(gameTrigger).toContainText("原神");
    await expect(plugin.locator('select[data-plugin-field="games"]')).toHaveCount(0);
    await gameTrigger.click();
    const gameMenu = plugin.locator(".plugin-multi-select-menu");
    await expect(gameMenu).toBeVisible();
    await gameMenu.locator('input[value="zzz"]').check();
    await expect(gameTrigger).toContainText("绝区零");
    await plugin.getByLabel("HoYoLAB Cookie", { exact: true }).fill("ltuid=1; ltoken=secret");
    await globalDialog.getByRole("button", { name: "保存", exact: true }).click();
    await expect(globalDialog).toBeHidden();
    expect(savedPluginPayload?.values).toEqual({
      enabled: true,
      cookie: { action: "set", value: "ltuid=1; ltoken=secret" },
      games: ["gi", "zzz"],
    });

    await card.getByRole("button", { name: "用户管理", exact: true }).click();
    const dialog = page.getByRole("dialog", { name: "用户管理" });
    await expect(dialog.getByTestId("um-binding-card")).toHaveCount(1);
    const binding = dialog.getByTestId("um-binding-card").first();
    await binding.locator('[data-action="toggle-um-binding"]').click();
    await binding.locator('[data-action="set-um-subview"][data-view="notify"]').click();
    await dialog.getByLabel("SMTP 收件人", { exact: true }).fill("new@example.com");
    await dialog.getByRole("button", { name: "返回上级", exact: true }).click();
    await dialog.getByRole("button", { name: "保存", exact: true }).click();
    await expect(dialog).toBeHidden();

    const updated = await (await api("GET", `/api/users/${encodeURIComponent(user.id)}`)).json();
    const updatedBinding = updated.bindings.find(item => item.scriptInstanceId === script.id);
    expect(updatedBinding.smtpTo).toBe("new@example.com");
  } finally {
    await api("DELETE", `/api/users/${encodeURIComponent(user.id)}`, { confirmName: user.name });
    await api("DELETE", `/api/scripts/${encodeURIComponent(script.id)}`);
  }
});

test("模拟器表单入口：切换启动方式后字段和保存契约一致", async ({ page }) => {
  const fixture = makeScriptDir("smoke-emulator-form");
  await page.goto(baseUrl + "#/scripts", { waitUntil: "domcontentloaded" });
  await page.getByTestId("new-script").click();
  await page.locator('.new-script-chooser [data-action="open-script-type"][data-plugin=""]').click();
  await page.selectOption("#sm-mode", "emulator");
  await expect(page.locator('label[for="sm-game-exe"]')).toContainText("模拟器ADB地址");
  await page.locator("#sm-name").fill("Smoke 模拟器表单");
  await page.locator("#sm-root").fill(fixture.root);
  await page.locator("#sm-exe").fill(fixture.main);
  await page.locator("#sm-config").fill(fixture.cfg);
  await page.locator("#sm-log").fill(fixture.log);
  await page.locator("#sm-game-exe").fill("127.0.0.1:16384");
  await page.locator("#sm-game-args").fill("-n com.example.game/.MainActivity");
  await page.locator(".modal").getByRole("button", { name: "保存", exact: true }).click();
  const card = page.getByTestId("script-card").filter({ hasText: "Smoke 模拟器表单" }).first();
  await expect(card).toBeVisible();
  const scripts = await (await api("GET", "/api/scripts")).json();
  const created = scripts.find(item => item.name === "Smoke 模拟器表单");
  expect(created.gameMode).toBe("emulator");
  await api("DELETE", `/api/scripts/${encodeURIComponent(created.id)}`);
});
