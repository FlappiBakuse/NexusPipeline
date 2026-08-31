import { test, expect } from "@playwright/test";
import { api, baseUrl } from "./helpers.mjs";

test("应用入口：仪表盘显示版本与服务状态", async ({ page }) => {
  await page.goto(baseUrl + "#/dashboard", { waitUntil: "domcontentloaded" });
  const state = page.getByTestId("dashboard-state");
  await expect(state).toBeVisible();
  const status = await (await api("GET", "/api/status")).json();
  await expect(page.locator("body")).toContainText(status.version);
  await expect(page.locator("body")).toContainText("当前版本");
});

test("主导航：核心页面可以按路由打开", async ({ page }) => {
  const date = "2026-08-30";
  const secondDate = "2026-08-29";
  let historyRecordRequests = 0;
  await page.route("**/api/history/dates**", async route => {
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ dates: [{ date, count: 1 }, { date: secondDate, count: 1 }] }),
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
          finalStatus: "skipped",
          attempts: 0,
          maxAttempts: 1,
          resultDetail: "达到每日成功运行次数上限",
          attemptDetails: [],
        }],
      }),
    });
  });
  await page.goto(baseUrl + "#/dashboard", { waitUntil: "domcontentloaded" });
  for (const route of ["users", "scripts", "queues", "dispatch", "history", "plugins", "settings"]) {
    await page.getByTestId(`nav-${route}`).click();
    await page.waitForFunction(expected => location.hash === `#/${expected}`, route);
    await expect(page.locator("#view")).toBeVisible();
  }
  await page.setViewportSize({ width: 768, height: 900 });
  await page.goto(baseUrl + "#/history", { waitUntil: "domcontentloaded" });
  await expect(page.locator('[data-testid="history-date"]').first()).toBeVisible();
  await expect(page.getByTestId("history-records-count")).toHaveText("选择用户");
  await expect(page.locator(".history-browser")).not.toHaveClass(/history-detail-visible/);
  await page.locator(`[data-testid="history-date"][data-date="${date}"]`).click();
  await expect(page.locator(".history-browser")).toHaveClass(/history-users-visible/);
  await expect(page.locator(`[data-testid="history-date-users"][data-date="${date}"]`).getByTestId("history-user")).toBeVisible();
  await expect(page.locator(".history-records-column")).toBeHidden();
  await page.locator(`[data-testid="history-date"][data-date="${secondDate}"]`).click();
  await expect(page.locator('[data-testid="history-date-users"]')).toHaveCount(2);
  await expect(page.locator(`[data-testid="history-date-users"][data-date="${secondDate}"]`).getByTestId("history-user")).toBeVisible();
  await page.locator(`[data-testid="history-date"][data-date="${date}"]`).click();
  await expect(page.locator('[data-testid="history-date-users"]')).toHaveCount(1);
  await expect(page.locator(`[data-testid="history-date-users"][data-date="${secondDate}"]`)).toBeVisible();
  await page.locator(`[data-testid="history-date-users"][data-date="${secondDate}"]`).getByTestId("history-user").click();
  await expect(page.locator(".history-browser")).toHaveClass(/history-detail-visible/);
  await expect(page.getByText("已跳过", { exact: true })).toBeVisible();
  const requestsAfterInitialUser = historyRecordRequests;
  await page.setViewportSize({ width: 1280, height: 900 });
  await expect(page.getByRole("button", { name: "返回用户列表", exact: true })).toBeHidden();
  await page.locator(`[data-testid="history-date"][data-date="${date}"]`).click();
  await expect(page.getByTestId("history-entry")).toBeVisible();
  expect(historyRecordRequests).toBe(requestsAfterInitialUser);
  await page.locator(`[data-testid="history-date-users"][data-date="${secondDate}"]`).getByTestId("history-user").click();
  await expect.poll(() => historyRecordRequests).toBe(requestsAfterInitialUser + 1);
  await expect(page.getByTestId("history-entry")).toBeVisible();
  await page.setViewportSize({ width: 768, height: 900 });
  await expect(page.getByRole("button", { name: "返回用户列表", exact: true })).toBeVisible();
  await page.getByRole("button", { name: "返回用户列表", exact: true }).click();
  await expect(page.locator(".history-browser")).not.toHaveClass(/history-detail-visible/);
  await expect(page.locator(".history-browser")).toHaveClass(/history-users-visible/);
  await expect(page.locator(`[data-testid="history-date-users"][data-date="${secondDate}"]`).getByTestId("history-user")).toBeVisible();
});

test("手机宽度：仪表盘无横向溢出且导航抽屉可开关", async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 800 });
  try {
    await page.goto(baseUrl + "#/dashboard", { waitUntil: "domcontentloaded" });
    await expect(page.getByTestId("dashboard-state")).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBeTruthy();
    await page.getByRole("button", { name: "打开导航" }).click();
    await expect(page.locator("body")).toHaveClass(/nav-open/);
    await page.getByRole("button", { name: "关闭导航" }).click({ force: true });
    await expect(page.locator("body")).not.toHaveClass(/nav-open/);
  } finally {
    await page.setViewportSize({ width: 1280, height: 900 });
  }
});
