import { test, expect } from "@playwright/test";
import { baseUrl } from "./helpers.mjs";

test("固定视口：主壳与所有核心页面保持视觉契约", async ({ page }) => {
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
    await page.evaluate(() => document.getElementById("ambient-particles")?.remove());
    await page.evaluate(() => document.fonts?.ready);
    await page.waitForTimeout(100);
    await expect(page).toHaveScreenshot(`visual-${route}.png`, screenshotOptions);
  }
});
