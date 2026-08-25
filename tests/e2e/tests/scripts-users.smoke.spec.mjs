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
  const userResponse = await api("POST", "/api/users", { name: `Smoke 绑定用户-${suffix}`, autoCheckInEnabled: false });
  const user = await userResponse.json();
  await api("POST", `/api/users/${encodeURIComponent(user.id)}/bindings`, {
    scriptInstanceId: script.id,
    enabled: true,
    notifyEnabled: true,
    smtpTo: "old@example.com",
  });

  try {
    await page.goto(baseUrl + "#/users", { waitUntil: "domcontentloaded" });
    const card = page.getByTestId("global-user-card").filter({ hasText: user.name }).first();
    await card.getByRole("button", { name: "用户管理", exact: true }).click();
    const dialog = page.getByRole("dialog", { name: "用户管理" });
    await expect(dialog.getByTestId("um-binding-card")).toHaveCount(1);
    const binding = dialog.getByTestId("um-binding-card").first();
    await binding.locator('[data-action="toggle-um-binding"]').click();
    await binding.locator('[data-action="set-um-subview"][data-view="notify"]').click();
    await dialog.getByLabel("SMTP 收件人", { exact: true }).fill("new@example.com");
    await binding.getByRole("button", { name: "返回上级", exact: true }).click();
    await dialog.getByRole("button", { name: "保存设置", exact: true }).click();
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
