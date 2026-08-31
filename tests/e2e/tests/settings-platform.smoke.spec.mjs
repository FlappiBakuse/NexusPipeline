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

test("远程访问开关：切换后同步 aria 与 data-state", async ({ page }) => {
  await page.goto(baseUrl + "#/settings", { waitUntil: "domcontentloaded" });
  await page.locator('[data-action="toggle-settings-panel"][data-panel="remote-mcp"]').click();
  const toggle = page.locator("#st-remote");
  await expect(toggle).toBeVisible();
  const original = (await toggle.getAttribute("aria-pressed")) === "true";
  const next = !original;
  await toggle.click();
  await expect(toggle).toHaveAttribute("aria-pressed", String(next));
  await expect(toggle).toHaveAttribute("data-state", next ? "on" : "off");
  await expect.poll(async () => (await (await api("GET", "/api/settings")).json()).settings.allowRemoteAccess, { timeout: 10000 }).toBe(next);
  await toggle.click();
  await expect(toggle).toHaveAttribute("aria-pressed", String(original));
  await expect(toggle).toHaveAttribute("data-state", original ? "on" : "off");
  await expect.poll(async () => (await (await api("GET", "/api/settings")).json()).settings.allowRemoteAccess, { timeout: 10000 }).toBe(original);
});

test("插件页面：双栏浏览器加载本地与仓库列表", async ({ page }) => {
  await page.goto(baseUrl + "#/plugins", { waitUntil: "domcontentloaded" });
  await expect(page.getByTestId("plugin-browser")).toBeVisible();
  await expect(page.getByTestId("plugin-local-tab")).toHaveAttribute("aria-selected", "true");
  await expect(page.getByTestId("plugin-local-list")).toBeVisible();
  await expect(page.getByTestId("plugin-detail")).toBeVisible();
  await expect(page.locator(".plugin-detail-meta")).not.toContainText("Frontend API");
  const search = page.getByTestId("plugin-search");
  await expect(search).toBeVisible();
  await page.getByTestId("plugin-store-tab").click();
  await expect(page.getByTestId("plugin-store-list")).toBeVisible();
  await expect(page.getByTestId("plugin-detail")).toBeVisible();
  await expect(page.locator(".plugin-detail-meta")).not.toContainText("Frontend API");
  const refresh = page.getByTestId("plugin-store-refresh");
  await expect(refresh).toBeVisible();
  await search.fill("不存在的插件关键词");
  await expect(page.getByTestId("plugin-store-list")).toContainText("没有匹配的插件");
});
