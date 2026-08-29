import { test, expect } from "@playwright/test";
import { api, baseUrl } from "./helpers.mjs";

test("设置入口：保存普通服务设置并从 API 读取", async ({ page }) => {
  await page.goto(baseUrl + "#/settings", { waitUntil: "domcontentloaded" });
  await expect(page.locator("#st-retention")).toBeVisible();
  await expect(page.locator('[data-action="toggle-settings-panel"][data-panel="service"]')).toHaveAttribute("aria-expanded", "true");
  await expect(page.getByTestId("mcp-settings")).toBeVisible();
  await expect(page.locator("#st-mcp-port")).toBeHidden();
  const current = Number(await page.locator("#st-retention").inputValue());
  const next = current === 7 ? 8 : 7;
  await page.locator("#st-retention").fill(String(next));
  await page.locator("#st-retention").dispatchEvent("change");
  await expect.poll(async () => (await (await api("GET", "/api/settings")).json()).settings.historyRetentionDays, { timeout: 10000 }).toBe(next);
  await page.locator('[data-action="toggle-settings-panel"][data-panel="remote-mcp"]').click();
  await expect(page.locator("#st-mcp-port")).toHaveValue("58732");
});

test("访问令牌入口：生成、显示切换和状态回读", async ({ page }) => {
  await page.goto(baseUrl + "#/settings", { waitUntil: "domcontentloaded" });
  await page.locator('[data-action="toggle-settings-panel"][data-panel="remote-mcp"]').click();
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
  await expect(page.getByTestId("plugin-local-tab")).toHaveAttribute("aria-selected", "true");
  const tabOrder = await page.locator('.plugin-tabs [role="tab"]').evaluateAll(tabs => tabs.map(tab => tab.dataset.tab));
  expect(tabOrder).toEqual(["local", "store"]);
  await expect(page.getByTestId("plugins-list")).toBeVisible();
  await page.getByTestId("plugin-store-tab").click();
  await expect(page.getByTestId("plugin-store-list")).toBeVisible();
  await expect(page.locator('[data-testid="plugin-store-status"]').first()).toBeVisible();
  const groupTitles = page.getByTestId("plugin-store-list").locator(".plugin-group-heading h3");
  await expect(groupTitles.first()).toHaveText("通用插件");
  await expect(groupTitles.nth(1)).toHaveText("专项插件");
});
