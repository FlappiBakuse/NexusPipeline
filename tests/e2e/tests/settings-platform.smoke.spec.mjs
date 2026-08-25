import { test, expect } from "@playwright/test";
import { api, baseUrl } from "./helpers.mjs";

test("设置入口：保存普通服务设置并从 API 读取", async ({ page }) => {
  await page.goto(baseUrl + "#/settings", { waitUntil: "domcontentloaded" });
  await expect(page.locator("#st-retention")).toBeVisible();
  const current = Number(await page.locator("#st-retention").inputValue());
  const next = current === 7 ? 8 : 7;
  await page.locator("#st-retention").fill(String(next));
  await page.locator("#st-retention").dispatchEvent("change");
  await expect.poll(async () => (await (await api("GET", "/api/settings")).json()).settings.historyRetentionDays, { timeout: 10000 }).toBe(next);
});

test("访问令牌入口：生成、显示切换和状态回读", async ({ page }) => {
  await page.goto(baseUrl + "#/settings", { waitUntil: "domcontentloaded" });
  const token = page.locator("#st-token");
  await page.getByTestId("gen-token").click();
  await expect.poll(async () => (await token.inputValue()).length, { timeout: 10000 }).toBeGreaterThan(20);
  await expect(token).toHaveAttribute("type", "password");
  await page.getByTestId("toggle-token-visibility").click();
  await expect(token).toHaveAttribute("type", "text");
  const settings = await (await api("GET", "/api/settings")).json();
  expect(settings.status.remote.tokenSet).toBeTruthy();
});

test("插件页面：健康状态以列表形式加载", async ({ page }) => {
  await page.goto(baseUrl + "#/plugins", { waitUntil: "domcontentloaded" });
  await expect(page.locator(".plugins-table")).toBeVisible();
  await expect(page.locator(".plugin-group").first()).toBeVisible();
  await expect(page.locator('[data-testid="plugin-status"]').first()).toBeVisible();
});

test("设置页面：手机宽度无溢出且通知面板可展开", async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 800 });
  try {
    await page.goto(baseUrl + "#/settings", { waitUntil: "domcontentloaded" });
    await expect(page.locator("#st-retention")).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBeTruthy();
    await page.getByRole("button", { name: /SMTP 邮件通知/ }).click();
    await expect(page.locator("#panel-smtp")).toBeVisible();
  } finally {
    await page.setViewportSize({ width: 1280, height: 900 });
  }
});
