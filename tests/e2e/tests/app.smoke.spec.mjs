import { test, expect } from "@playwright/test";
import fs from "node:fs/promises";
import { api, baseUrl } from "./helpers.mjs";

test("连续快速切换导航后仍可打开每个页面", async ({ page }, testInfo) => {
  const pages = [
    ["dashboard", "仪表盘"], ["users", "用户管理"], ["scripts", "脚本实例"],
    ["queues", "调度队列"], ["dispatch", "调度中心"], ["history", "历史记录"],
    ["plugins", "插件"], ["settings", "设置"],
  ];
  const errors = [];
  const navigation = [];
  page.on("pageerror", error => errors.push(error.stack || error.message));
  await page.goto(baseUrl + "#/dashboard", { waitUntil: "domcontentloaded" });
  await expect(page.getByTestId("dashboard-state")).toBeVisible();
  let seed = 1615;
  let previous = "dashboard";
  try {
    for (let burst = 0; burst < 16; burst += 1) {
      for (let step = 0; step < 8; step += 1) {
        seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0;
        let index = (seed >>> 16) % pages.length;
        if (pages[index][0] === previous) index = (index + 1) % pages.length;
        previous = pages[index][0];
        navigation.push(previous);
        // 不等内容区完成加载或动画，覆盖离场尚未结束时的下一次导航。
        await page.getByTestId(`nav-${previous}`).click();
      }
      const title = pages.find(([path]) => path === previous)[1];
      await expect(page.getByTestId(`nav-${previous}`)).toHaveAttribute("aria-current", "page");
      await expect(page.getByRole("heading", { name: title, exact: true })).toBeVisible();
    }
    for (const [path, title] of pages) {
      await page.getByTestId(`nav-${path}`).click();
      await expect(page.getByRole("heading", { name: title, exact: true })).toBeVisible();
    }
    expect(errors).toEqual([]);
  } finally {
    const evidencePath = testInfo.outputPath("rapid-navigation.json");
    await fs.writeFile(evidencePath, JSON.stringify({ seed: 1615, navigation, errors }, null, 2));
    await testInfo.attach("rapid-navigation", {
      path: evidencePath, contentType: "application/json",
    });
  }
});

test("主导航：核心页面可以按路由打开", async ({ page }) => {
  const date = "2026-08-30";
  const secondDate = "2026-08-29";
  let historyRecordRequests = 0;
  const taskReport = {
    schemaVersion: 2, semanticsVersion: "daily-flow-v1", runId: "history-narrow-smoke", revision: 1, lifecycleOutcome: "completed",
    originalPlan: { protocolVersion: "0.2.0", coverage: "complete", diagnostics: [{ code: "fixture.option", taskId: "parent", message: "fallback",
      reasonText: { kind: "literal", value: "邮件：已关闭（配置选项，无独立完成证据） <em>plain</em> " + "LongOption".repeat(25) } }], tasks: [
      { id: "parent", name: "第九插件任务", enabled: true, parentId: null, role: "business", countsAsUnit: true, order: 0, detection: "supported", retryRisk: "safe" },
      { id: "child", name: "子任务", enabled: true, parentId: "parent", role: "business", countsAsUnit: false, order: 1, detection: "supported", retryRisk: "safe" }
    ] },
    finalTaskResults: [{ taskId: "parent", status: "partial" }, { taskId: "child", status: "failed" }],
    summary: { tone: "warn", counts: { total: 1, succeeded: 0, skipped: 0, partial: 1 } },
    attemptReports: [{ attemptId: "a1", number: 1, selectedTaskIds: ["parent", "child"], taskResults: [
      { taskId: "child", status: "failed", reasonCode: "fixture", reasonText: { kind: "literal", value: "冻结失败原因 <b>plain</b>" }, evidence: [
        { sourceId: "stdout", epoch: 1, sequence: 1, ruleId: "fixture" }
      ] }
    ] }],
    evidenceLines: [{ attemptId: "a1", sourceId: "stdout", epoch: 1, sequence: 1, text: "LongEvidence".repeat(50) }]
  };
  await page.route("**/api/history/detail?**", route => route.fulfill({ json: {
    record: { id: "history-narrow-smoke", scriptName: "窄屏历史脚本", startTime: `${date}T08:00:00`,
      endTime: `${date}T08:00:01`, status: "partial", attempts: 1, taskReport, attemptDetails: [] }, attemptLogs: []
  } }));
  await page.route("**/api/history/dates**", async route => {
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ dates: [{ date, count: 1 }, { date: secondDate, count: 1 }] }),
    });
  });
  await page.route("**/api/history/summary**", async route => {
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({
        totalCount: 2,
        statusCounts: { success: 0, failed: 0, partial: 0, cancelled: 0, skipped: 2 },
        totalDurationMs: 2000,
        averageDurationMs: 1000,
        successRate: 0,
        daily: [{ date, totalCount: 1, statusCounts: { success: 0, failed: 0, partial: 0, cancelled: 0, skipped: 1 }, totalDurationMs: 1000, averageDurationMs: 1000 }],
      }),
    });
  });
  await page.route("**/api/history/users?date=**", async route => {
    const requestDate = new URL(route.request().url()).searchParams.get("date") || date;
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({
        date: requestDate,
        users: [{ userKey: `id:smoke-user-${requestDate}`, userId: `smoke-user-${requestDate}`, userName: requestDate === date ? "窄屏用户" : "第二日期用户", count: 1, successCount: 0, failedCount: 0, partialCount: 0, cancelledCount: 0, skippedCount: 1 }],
      }),
    });
  });
  await page.route("**/api/history?date=**", async route => {
    historyRecordRequests += 1;
    const requestUrl = new URL(route.request().url());
    const requestDate = requestUrl.searchParams.get("date") || date;
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({
        historyDir: requestDate,
        records: [{
          id: "history-narrow-smoke",
          scriptName: "窄屏历史脚本",
          startTime: `${requestDate}T08:00:00`,
          endTime: `${requestDate}T08:00:01`,
          mode: "manual",
          status: "skipped",
          attempts: 0,
          maxAttempts: 1,
          resultDetail: "达到每日成功运行次数上限",
          attemptDetails: [],
        }],
      }),
    });
  });
  await page.goto(baseUrl + "#/dashboard", { waitUntil: "domcontentloaded" });
  await expect(page.getByTestId("dashboard-state")).toBeVisible();
  await expect(page.getByTestId("dashboard-history-summary-statuses")).toBeVisible();
  await expect(page.locator("[data-testid='dashboard-history-summary-statuses'] [data-status='skipped']")).toContainText("2");
  await expect(page.getByTestId("dashboard-history-summary-performance")).toBeVisible();
  await expect(page.getByTestId("nav-dashboard")).toHaveAttribute("aria-current", "page");
  await expect(page.getByRole("heading", { name: "仪表盘", exact: true })).toBeVisible();
  await expect(page.locator("body")).not.toContainText("signal is aborted without reason");
  for (const route of ["users", "scripts", "queues", "dispatch", "history", "plugins", "settings"]) {
    await page.getByTestId(`nav-${route}`).click();
    await page.waitForFunction(expected => location.hash === `#/${expected}`, route);
    await expect(page.locator("#view")).toBeVisible();
  }
  await page.setViewportSize({ width: 768, height: 900 });
  await page.goto(baseUrl + "#/history", { waitUntil: "domcontentloaded" });
  await expect(page.locator('[data-testid="history-date"]').first()).toBeVisible();
  await expect(page.getByTestId("history-query-toolbar")).toBeVisible();
  await expect(page.getByTestId("history-status-filter")).toBeVisible();
  await expect(page.getByText("状态筛选", { exact: true })).toHaveCount(0);
  await expect(page.locator('[data-testid="history-summary"]')).toHaveCount(0);
  await expect(page.getByTestId("history-records-count")).toBeVisible();
  await expect(page.getByTestId("history-records-count")).toHaveText("选择用户");
  await page.locator(`[data-testid="history-date"][data-date="${date}"]`).click();
  await expect(page.locator(`[data-testid="history-date-users"][data-date="${date}"]`).getByTestId("history-user")).toBeVisible();
  await page.locator(`[data-testid="history-date"][data-date="${secondDate}"]`).click();
  await expect(page.locator('[data-testid="history-date-users"]')).toHaveCount(2);
  await expect(page.locator(`[data-testid="history-date-users"][data-date="${secondDate}"]`).getByTestId("history-user")).toBeVisible();
  await page.locator(`[data-testid="history-date"][data-date="${date}"]`).click();
  await expect(page.locator('[data-testid="history-date-users"]')).toHaveCount(1);
  await expect(page.locator(`[data-testid="history-date-users"][data-date="${secondDate}"]`)).toBeVisible();
  await page.locator(`[data-testid="history-date-users"][data-date="${secondDate}"]`).getByTestId("history-user").click();
  await expect(page.getByTestId("history-entry").getByText("已跳过", { exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "返回用户列表", exact: true })).toBeHidden();
  const requestsAfterInitialUser = historyRecordRequests;
  for (const width of [360, 768, 1280]) {
    await page.setViewportSize({ width, height: 900 });
    await expect(page.getByTestId("history-entry")).toBeVisible();
    await expect(page.getByTestId("history-records-count")).toHaveText("1 条记录");
    expect(historyRecordRequests).toBe(requestsAfterInitialUser);
  }
  await page.setViewportSize({ width: 1280, height: 900 });
  await expect(page.getByRole("button", { name: "返回用户列表", exact: true })).toBeHidden();
  await page.locator(`[data-testid="history-date"][data-date="${date}"]`).click();
  await expect(page.getByTestId("history-entry")).toBeVisible();
  expect(historyRecordRequests).toBe(requestsAfterInitialUser);
  await page.getByTestId("history-entry").click();
  const detail = page.getByRole("dialog");
  for (const width of [360, 768, 1280]) {
    await page.setViewportSize({ width, height: 900 });
    const notes = detail.getByRole("list", { name: "计划说明", exact: true });
    await expect(notes).toContainText("邮件：已关闭（配置选项，无独立完成证据） <em>plain</em>");
    await expect(notes.locator("em")).toHaveCount(0);
    const parent = detail.getByRole("button", { name: /第九插件任务/ });
    await parent.focus();
    await expect(parent).toBeFocused();
    if (await parent.getAttribute("aria-expanded") !== "true") await parent.press("Enter");
    await expect(parent).toHaveAttribute("aria-expanded", "true");
    const child = detail.getByRole("button", { name: /子任务/ });
    await child.focus();
    if (await child.getAttribute("aria-expanded") !== "true") await child.press("Space");
    await expect(child).toHaveAttribute("aria-expanded", "true");
    await expect(detail.getByText("失败 · 冻结失败原因 <b>plain</b>", { exact: true })).toBeVisible();
  }
  await page.keyboard.press("Escape");
  await expect(detail).toBeHidden();
  await page.locator(`[data-testid="history-date-users"][data-date="${secondDate}"]`).getByTestId("history-user").click();
  await expect.poll(() => historyRecordRequests).toBe(requestsAfterInitialUser + 1);
  await expect(page.getByTestId("history-entry")).toBeVisible();
  await page.setViewportSize({ width: 600, height: 900 });
  await expect(page.getByRole("button", { name: "返回用户列表", exact: true })).toBeVisible();
  await page.getByRole("button", { name: "返回用户列表", exact: true }).click();
  await expect(page.locator(`[data-testid="history-date-users"][data-date="${secondDate}"]`).getByTestId("history-user")).toBeVisible();
});

test("手机导航：抽屉可开关并同步无障碍状态", async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 800 });
  try {
    await page.goto(baseUrl + "#/dashboard", { waitUntil: "domcontentloaded" });
    await expect(page.getByTestId("dashboard-state")).toBeVisible();
    const sidebar = page.locator("#sidebar");
    const openNav = page.getByRole("button", { name: "打开导航" });
    await openNav.click();
    await expect(openNav).toHaveAttribute("aria-expanded", "true");
    await expect(sidebar).toHaveAttribute("aria-hidden", "false");
    await page.getByRole("button", { name: "关闭导航" }).click({ force: true });
    await expect(openNav).toHaveAttribute("aria-expanded", "false");
    await expect(sidebar).toHaveAttribute("aria-hidden", "true");
  } finally {
    await page.setViewportSize({ width: 1280, height: 900 });
  }
});
