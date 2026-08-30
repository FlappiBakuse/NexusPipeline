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

test("插件页面：双栏浏览器加载本地与仓库列表", async ({ page }) => {
  await page.goto(baseUrl + "#/plugins", { waitUntil: "domcontentloaded" });
  await expect(page.getByTestId("plugin-browser")).toBeVisible();
  await expect(page.getByTestId("plugin-local-tab")).toHaveAttribute("aria-selected", "true");
  const tabOrder = await page.locator('.plugin-tabs [role="tab"]').evaluateAll(tabs => tabs.map(tab => tab.dataset.tab));
  expect(tabOrder).toEqual(["local", "store"]);
  await expect(page.getByTestId("plugin-local-list")).toBeVisible();
  await expect(page.getByTestId("plugin-detail")).toBeVisible();
  await expect(page.locator(".plugin-detail-meta")).not.toContainText("Frontend API");
  const search = page.getByTestId("plugin-search");
  await expect(search).toBeVisible();
  await expect(search).toHaveAttribute("placeholder", "搜索插件名称、标签或游戏");
  const searchLayout = await search.evaluate(input => ({
    inListColumn: !!input.closest(".plugin-list-column"),
    inListCard: !!input.closest(".plugin-list-pane"),
    hasVisibleLabel: !!input.closest(".plugin-search")?.querySelector(".field-label"),
  }));
  expect(searchLayout.inListColumn && !searchLayout.inListCard && !searchLayout.hasVisibleLabel, "搜索框位于左栏卡片外并使用占位提示").toBeTruthy();
  const columnLayout = await page.getByTestId("plugin-browser").evaluate(browser => {
    const listColumn = browser.querySelector(".plugin-list-column");
    const detailColumn = browser.querySelector(".plugin-detail-column");
    const listPane = browser.querySelector(".plugin-list-pane");
    const detailPane = browser.querySelector(".plugin-detail-pane");
    const searchRect = browser.querySelector(".plugin-search")?.getBoundingClientRect();
    const detailRect = detailPane?.getBoundingClientRect();
    const listRect = listPane?.getBoundingClientRect();
    return {
      columnHeightDelta: Math.abs((listColumn?.getBoundingClientRect().height || 0) - (detailColumn?.getBoundingClientRect().height || 0)),
      searchDetailTopDelta: Math.abs((searchRect?.top || 0) - (detailRect?.top || 0)),
      searchListGap: (listRect?.top || 0) - (searchRect?.bottom || 0),
      listOverflow: listPane ? getComputedStyle(listPane).overflowY : "",
      detailOverflow: detailPane ? getComputedStyle(detailPane).overflowY : "",
    };
  });
  expect(columnLayout.columnHeightDelta).toBeLessThanOrEqual(1);
  expect(columnLayout.searchDetailTopDelta).toBeLessThanOrEqual(1);
  expect(columnLayout.searchListGap).toBeGreaterThanOrEqual(8);
  expect(columnLayout.listOverflow).toBe("auto");
  expect(columnLayout.detailOverflow).toBe("auto");
  await page.getByTestId("plugin-store-tab").click();
  await expect(page.getByTestId("plugin-store-list")).toBeVisible();
  await expect(page.getByTestId("plugin-detail")).toBeVisible();
  await expect(page.locator(".plugin-detail-meta")).not.toContainText("Frontend API");
  const refresh = page.getByTestId("plugin-store-refresh");
  await expect(refresh).toBeVisible();
  const refreshLayout = await refresh.evaluate(button => ({
    inDetailCard: !!button.closest(".plugin-detail-pane"),
    inListCard: !!button.closest(".plugin-list-pane"),
    inDetailColumn: !!button.closest(".plugin-detail-column"),
  }));
  expect(refreshLayout.inDetailColumn && !refreshLayout.inDetailCard && !refreshLayout.inListCard, "刷新仓库位于右栏卡片外").toBeTruthy();
  await search.fill("不存在的插件关键词");
  await expect(page.getByTestId("plugin-store-list")).toContainText("没有匹配的插件");
});
