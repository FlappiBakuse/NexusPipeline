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
  await expect(page.getByTestId("plugin-store-tab")).toHaveAttribute("aria-selected", "true");
  await expect(page.getByTestId("plugin-store-list")).toBeVisible();
  await expect(page.locator('[data-testid="plugin-store-status"]').first()).toBeVisible();
});

test("设置页面：三档代理模式可切换并保存", async ({ page }) => {
  await page.goto(baseUrl + "#/settings", { waitUntil: "domcontentloaded" });
  await page.locator('[data-action="toggle-settings-panel"][data-panel="network"]').click();
  const mode = page.locator("#st-proxy-mode");
  await expect(mode).toBeVisible();
  await mode.selectOption("none");
  await expect.poll(async () => (await (await api("GET", "/api/settings")).json()).settings.proxyMode, { timeout: 10000 }).toBe("none");

  await mode.selectOption("http");
  await expect(page.locator("#st-proxy-custom")).toBeVisible();
  await page.locator("#st-proxy-url").fill("http://127.0.0.1:7890");
  await page.locator("#st-proxy-url").blur();
  await expect.poll(async () => {
    const settings = (await (await api("GET", "/api/settings")).json()).settings;
    return `${settings.proxyMode}|${settings.proxyUrl}`;
  }, { timeout: 10000 }).toBe("http|http://127.0.0.1:7890");

  await mode.selectOption("system");
  await expect.poll(async () => (await (await api("GET", "/api/settings")).json()).settings.proxyMode, { timeout: 10000 }).toBe("system");
  await mode.selectOption("none");
  await expect.poll(async () => (await (await api("GET", "/api/settings")).json()).settings.proxyMode, { timeout: 10000 }).toBe("none");
});

test("设置页面：手机宽度无溢出且通知面板可展开", async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 800 });
  try {
    await page.goto(baseUrl + "#/settings", { waitUntil: "domcontentloaded" });
    await expect(page.locator("#st-retention")).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBeTruthy();
    await page.locator('[data-action="toggle-settings-panel"][data-panel="notifications"]').click();
    await page.getByRole("button", { name: /SMTP 邮件通知/ }).click();
    await expect(page.locator("#panel-smtp")).toBeVisible();
  } finally {
    await page.setViewportSize({ width: 1280, height: 900 });
  }
});

test("更新区：检查→下载→就绪按钮流（本地 stub 更新源）", async ({ page }) => {
  await page.goto(baseUrl + "#/settings", { waitUntil: "domcontentloaded" });
  await expect(page.getByTestId("update-section")).toBeVisible();
  await page.locator('[data-action="toggle-settings-panel"][data-panel="updates"]').click();
  // 等待可能的启动自动检查结束（状态机空闲后再手动检查）。
  await expect.poll(async () => (await (await api("GET", "/api/update/status")).json()).state, { timeout: 20000 }).toBe("idle");
  await page.getByTestId("update-check").click();
  await expect(page.getByTestId("update-download")).toBeVisible({ timeout: 15000 });
  await page.getByTestId("update-download").click();
  await expect(page.getByTestId("update-apply")).toBeVisible({ timeout: 30000 });
  await expect(page.getByTestId("update-defer")).toBeVisible();
});

test("更新区：立即应用→服务重启→页面恢复（末位用例）", async ({ page }) => {
  await page.goto(baseUrl + "#/settings", { waitUntil: "domcontentloaded" });
  await page.locator('[data-action="toggle-settings-panel"][data-panel="updates"]').click();
  await expect(page.getByTestId("update-apply")).toBeVisible({ timeout: 30000 });
  await page.getByTestId("update-apply").click();
  // 应用后服务退出并重拉；重启窗口内连接被拒属预期，手写循环捕获异常重试
  // （expect.poll 回调抛错会立即失败，不能用于服务重启窗口探测）。
  let restored = false;
  const deadlinePoll = Date.now() + 120000;
  while (Date.now() < deadlinePoll) {
    try {
      if ((await fetch(baseUrl + "api/status", { cache: "no-store" })).ok) {
        restored = true;
        break;
      }
    } catch { /* 服务重启窗口 */ }
    await new Promise(resolve => setTimeout(resolve, 500));
  }
  expect(restored, "更新应用后服务未在超时时间内恢复").toBe(true);
  await page.reload({ waitUntil: "domcontentloaded" });
  await expect(page.getByTestId("update-section")).toBeVisible();
  // 新实例启动后状态为 idle（重启窗口内 API 可能仍短暂不可达，用 toPass 容忍异常重试）。
  await expect(async () => {
    expect((await (await api("GET", "/api/update/status")).json()).state).toBe("idle");
  }).toPass({ timeout: 15000 });
});
