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
  await page.goto(baseUrl + "#/dashboard", { waitUntil: "domcontentloaded" });
  for (const route of ["users", "scripts", "queues", "dispatch", "history", "plugins", "settings"]) {
    await page.getByTestId(`nav-${route}`).click();
    await page.waitForFunction(expected => location.hash === `#/${expected}`, route);
    await expect(page.locator("#view")).toBeVisible();
  }
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
