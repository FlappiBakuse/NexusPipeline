import { test, expect } from "@playwright/test";
import { baseUrl } from "./helpers.mjs";

test("固定视口：核心页面行为与稳定元件保持视觉契约", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.emulateMedia({ colorScheme: "dark", reducedMotion: "reduce" });
  await page.addInitScript(() => {
    const NativeDate = Date;
    const fixedNow = new NativeDate("2026-09-11T12:00:00+08:00").valueOf();
    class FixedDate extends NativeDate {
      constructor(...args) {
        super(args.length ? args[0] : fixedNow);
      }

      static now() {
        return fixedNow;
      }
    }
    Object.defineProperty(window, "Date", { configurable: true, value: FixedDate });
  });
  await page.route(/\/api\/history(?:\/|\?|$)/, async route => {
    if (route.request().method() !== "GET") {
      await route.continue();
      return;
    }
    const pathname = new URL(route.request().url()).pathname;
    const body = pathname.endsWith("/dates")
      ? { dates: [] }
      : pathname.endsWith("/users")
        ? { users: [] }
        : { historyDir: "", records: [] };
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify(body),
    });
  });

  let userFixtures = false;
  let queueFixtures = false;
  let pluginFixtures = false;
  const visualUser = {
    id: "visual-user",
    name: "视觉验收用户",
    remark: "用于交互状态验收",
    index: 0,
    bindingCount: 2,
    bindings: [
      {
        scriptInstanceId: "visual-script-a",
        scriptName: "视觉脚本 A",
        enabled: true,
        notifyEnabled: true,
        smtpTo: "",
        preRunScript: "",
        postRunScript: "",
        runDays: -1,
        maxSuccessfulRunsPerDay: -1,
      },
      {
        scriptInstanceId: "visual-script-b",
        scriptName: "视觉脚本 B",
        enabled: true,
        notifyEnabled: true,
        smtpTo: "",
        preRunScript: "",
        postRunScript: "",
        runDays: 0,
        maxSuccessfulRunsPerDay: -1,
      },
    ],
  };
  const visualScripts = [
    { id: "visual-script-a", name: "视觉脚本 A", index: 0 },
    { id: "visual-script-b", name: "视觉脚本 B", index: 1 },
    { id: "visual-script-c", name: "可添加脚本", index: 2 },
  ];
  await page.route(/\/api\/users(?:\?.*)?$/, async route => {
    if (!userFixtures || route.request().method() !== "GET") {
      await route.continue();
      return;
    }
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify([visualUser]) });
  });
  await page.route(/\/api\/scripts(?:\?.*)?$/, async route => {
    if (userFixtures) {
      await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(visualScripts) });
      return;
    }
    if (queueFixtures) {
      await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify([{ id: "visual-script-a", name: "视觉脚本 A" }]) });
      return;
    }
    await route.continue();
  });
  await page.route(/\/api\/status(?:\?.*)?$/, async route => {
    if (!userFixtures) {
      await route.continue();
      return;
    }
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ plugins: [] }) });
  });
  await page.route(/\/api\/plugin-contributions\/user-list-badges(?:\?.*)?$/, async route => {
    if (!userFixtures) {
      await route.continue();
      return;
    }
    await route.fulfill({ status: 200, contentType: "application/json", body: "[]" });
  });
  await page.route(/\/api\/users\/visual-user\/global-settings(?:\?.*)?$/, async route => {
    if (!userFixtures || route.request().method() !== "GET") {
      await route.continue();
      return;
    }
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ general: { syncEnabled: false, enabled: true, runDays: -1, maxSuccessfulRunsPerDay: -1 }, notification: { syncEnabled: false, notifyEnabled: true, smtpTo: "" }, advanced: { syncEnabled: false, preRunScript: "", preRunOnceOnly: false, postRunScript: "", postRunOnFinalOnly: false } }) });
  });
  await page.route(/\/api\/plugin-contributions\/user-global\/visual-user(?:\?.*)?$/, async route => {
    if (!userFixtures || route.request().method() !== "GET") {
      await route.continue();
      return;
    }
    await route.fulfill({ status: 200, contentType: "application/json", body: "[]" });
  });
  await page.route(/\/api\/scripts\/[^/]+\/icon(?:\?.*)?$/, async route => {
    if (!userFixtures) {
      await route.continue();
      return;
    }
    await route.fulfill({ status: 404, contentType: "application/json", body: "{}" });
  });
  await page.route(/\/api\/queues(?:\?.*)?$/, async route => {
    if (!queueFixtures || route.request().method() !== "GET") {
      await route.continue();
      return;
    }
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify([{ id: "visual-queue", name: "视觉调度队列", autoRunMode: "scheduled", completionAction: "none", notifyEnabled: true, tasks: [{ id: "visual-task", index: 0, scriptInstanceId: "visual-script-a" }], timeSets: [{ id: "visual-time-set", enabled: true, days: [1, 2, 3, 4, 5], time: "05:30" }] }]) });
  });
  await page.route(/\/api\/plugins\/store(?:\?.*)?$/, async route => {
    if (!pluginFixtures || route.request().method() !== "GET") {
      await route.continue();
      return;
    }
    await new Promise(resolve => setTimeout(resolve, 900));
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ available: true, stale: false, fetchedAt: "2026-09-11 12:00", plugins: [{ name: "visual-plugin", displayName: "视觉验收插件", description: "用于插件仓库视觉验收。", kind: "managed-code", version: "1.0.0", status: "not-installed", installed: false, compatible: true }] }) });
  });
  await page.route(/\/api\/plugins\/store\/visual-plugin\/detail(?:\?.*)?$/, async route => {
    if (!pluginFixtures) {
      await route.continue();
      return;
    }
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ name: "visual-plugin", displayName: "视觉验收插件", description: "用于插件仓库视觉验收。", kind: "managed-code", version: "1.0.0", status: "not-installed", installed: false, compatible: true, readmeAvailable: true, readmeMarkdown: "# 视觉验收插件", changelog: [] }) });
  });

  const pages = [
    ["dashboard", "dashboard-state"],
    ["users", "open-global-user-modal"],
    ["scripts", "new-script"],
    ["queues", "main-view"],
    ["dispatch", "dispatch-running"],
    ["history", "history-panels"],
    ["plugins", "plugin-browser"],
    ["settings", "settings-cards"],
  ];
  const screenshotOptions = {
    animations: "disabled",
    caret: "hide",
    mask: [page.locator("#local-addr"), page.locator("#app-version")],
    maskColor: "#142238",
  };
  for (const [route, readyTestId] of pages) {
    await page.goto(`${baseUrl}#/${route}`, { waitUntil: "domcontentloaded" });
    await expect(page.getByTestId(readyTestId)).toBeVisible();
    await expect(page.getByTestId("main-view")).toBeVisible();
  }

  const frontendFixtureCard = page.locator('[data-plugin-slot="settings.cards"] [data-testid="frontend-fixture-card"]');
  // 插件前端模块在页面加载后异步载入，首次进入设置页需要等待插件槽位渲染完成。
  const fixtureTimeout = { timeout: 20_000 };
  await page.goto(`${baseUrl}#/settings`, { waitUntil: "domcontentloaded" });
  await expect(frontendFixtureCard).toHaveCount(1, fixtureTimeout);
  await page.goto(`${baseUrl}#/dashboard`, { waitUntil: "domcontentloaded" });
  await expect(page.getByTestId("dashboard-state")).toBeVisible();
  await page.goto(`${baseUrl}#/settings`, { waitUntil: "domcontentloaded" });
  await expect(frontendFixtureCard).toHaveCount(1, fixtureTimeout);

  userFixtures = true;
  await page.goto(`${baseUrl}#/users`, { waitUntil: "domcontentloaded" });
  await expect(page.getByTestId("global-user-card")).toBeVisible();
  await page.getByRole("button", { name: "用户管理", exact: true }).click();
  const userDialog = page.getByRole("dialog", { name: "用户管理" });
  const bindingSection = userDialog.getByTestId("um-binding-section");
  await expect(bindingSection).toBeVisible();
  await expect(userDialog.getByTestId("um-binding-card")).toHaveCount(2);

  const firstBinding = userDialog.getByTestId("um-binding-card").first();
  await firstBinding.locator('[data-action="toggle-um-binding"]').click();
  await expect(bindingSection).toHaveClass(/um-section-expanding/);
  await expect(firstBinding.locator(".um-binding-body")).toBeVisible();
  await expect(userDialog.getByTestId("um-binding-card").nth(1)).not.toBeVisible();
  await expect(userDialog.getByTestId("um-add-script")).not.toBeVisible();
  await expect(userDialog.getByRole("button", { name: "编辑绑定", exact: true })).not.toBeVisible();
  await expect(firstBinding.locator(".um-binding-bottom-arrow")).toHaveAttribute("data-direction", "down");

  await firstBinding.locator('[data-action="toggle-um-binding"]').click();
  await userDialog.getByRole("button", { name: "编辑绑定", exact: true }).click();
  await expect(bindingSection).toHaveClass(/um-binding-editing/);
  await expect(userDialog.getByTestId("um-remove-binding").first()).toBeVisible();
  await expect(userDialog.locator(".um-binding-bottom-arrow").first()).not.toBeVisible();
  await expect(userDialog.locator(".um-binding-drag-handle").first()).toBeHidden();
  await expect(userDialog.getByTestId("um-add-script")).not.toBeVisible();

  await userDialog.getByRole("button", { name: "完成编辑", exact: true }).click();
  await userDialog.getByRole("button", { name: "取消", exact: true }).click();
  await page.getByRole("button", { name: "全局管理", exact: true }).click();
  const globalDialog = page.getByRole("dialog", { name: "全局管理" });
  await expect(globalDialog).toBeVisible();
  const helpTarget = globalDialog.locator(".global-management-card-wide .nxp-path").first();
  await expect(helpTarget).toHaveAttribute("data-help", /.+/);
  const helpInput = helpTarget.locator(".nxp-path-input");
  await helpInput.focus();
  await page.waitForTimeout(760);
  await expect(page.locator("body > .nxp-tooltip")).toBeVisible({ timeout: 1500 });
  await page.keyboard.press("Escape");
  await expect(page.locator("body > .nxp-tooltip")).toBeHidden();
  await globalDialog.getByRole("button", { name: "取消", exact: true }).click();

  pluginFixtures = true;
  await page.goto(`${baseUrl}#/plugins`, { waitUntil: "domcontentloaded" });
  await expect(page.getByTestId("plugin-local-tab")).toBeVisible();
  await page.getByTestId("plugin-store-tab").click();
  await expect(page.getByTestId("plugin-store-loading")).toBeVisible();
  await expect(page.getByTestId("plugin-store-loading")).toHaveScreenshot("visual-plugin-store-loading.png", screenshotOptions);
  await expect(page.getByTestId("plugin-store-row")).toBeVisible({ timeout: 3000 });

  queueFixtures = true;
  await page.goto(`${baseUrl}#/queues`, { waitUntil: "domcontentloaded" });
  await expect(page.getByTestId("queue-card")).toBeVisible();
  await expect(page.getByTestId("queue-next")).toHaveText("等待定时触发");
  await expect(page.getByTestId("nxp-pager")).toHaveCount(0);
  await expect(page.locator('[data-plugin-slot="queues.list.badges"]')).toHaveCount(1);
  await page.getByTestId("queue-card").getByRole("button", { name: "编辑队列", exact: true }).click();
  const queueDialog = page.getByRole("dialog", { name: "编辑调度队列" });
  await expect(queueDialog).toBeVisible();
  await expect(queueDialog.locator('[data-plugin-slot="queues.editor.sections"]')).toHaveCount(1);
  const timeSetToggle = queueDialog.getByTestId("queue-timeset-toggle");
  await expect(timeSetToggle).toHaveAttribute("aria-expanded", "true");
  await timeSetToggle.click();
  await expect(timeSetToggle).toHaveAttribute("aria-expanded", "false");
  await expect(queueDialog.getByTestId("queue-timeset-body")).toBeHidden();
  await timeSetToggle.click();
  await expect(queueDialog.getByTestId("queue-timeset-body")).toBeVisible();
  await queueDialog.locator("#qm-mode-trigger").click();
  await expect(page.locator("body > #qm-mode-menu")).toBeVisible();
  await expect(page.locator("body > #qm-mode-menu")).toHaveScreenshot("visual-select-open.png", screenshotOptions);
  await page.keyboard.press("Escape");
  await queueDialog.locator("#ts-time-0").click();
  await expect(page.locator("body > .nxp-time-popover")).toBeVisible();
  await expect(page.locator("body > .nxp-time-popover")).toHaveScreenshot("visual-timepicker-open.png", screenshotOptions);

  await page.goto(`${baseUrl}#/ui-lab?test=1`, { waitUntil: "domcontentloaded" });
  await expect(page.getByRole("heading", { name: "组件状态实验室", exact: true })).toBeVisible();
  // 元件实验室由路由按需加载，需等宿主平台与 Nexus UI 元件样式生效后再取基线。
  await expect(page.getByRole("button", { name: "主要操作", exact: true })).toBeVisible();
  await expect(page.locator(".nxp-field-label", { hasText: "文本输入" })).toBeVisible();
  await expect(page.locator(".ui-lab-grid")).toBeVisible();
  await page.evaluate(() => document.getElementById("ambient-particles")?.remove());
  await page.evaluate(() => document.fonts?.ready);
  await page.waitForTimeout(100);
  await expect(page).toHaveScreenshot("visual-ui-lab.png", screenshotOptions);
});
