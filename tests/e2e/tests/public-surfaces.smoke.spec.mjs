import { test, expect } from "@playwright/test";
import { baseUrl } from "./helpers.mjs";

test("公开无框容器：主题与窄屏保留表面，子控件支持焦点和反馈", async ({ page }) => {
  await page.goto(baseUrl + "#/settings", { waitUntil: "domcontentloaded" });
  for (let theme = 0; theme < 3; theme++) {
    await page.evaluate(() => document.querySelector("[data-testid='public-surface-fixture-root']")?.remove());
    await page.getByRole("button", { name: "切换主题", exact: true }).last().click();
    await page.evaluate(() => {
      document.querySelector("[data-testid='public-surface-fixture-root']")?.remove();
      const fixture = document.createElement("div");
      fixture.dataset.testid = "public-surface-fixture-root";
      Object.assign(fixture.style, { position: "fixed", left: "16px", right: "16px", bottom: "16px", zIndex: "999" });
      const card = document.createElement("nxp-card");
      card.dataset.testid = "public-surface-fixture";
      card.as = "article";
      card.unstyled = true;
      const action = document.createElement("nxp-button");
      action.textContent = "公开容器操作";
      card.append(action);
      fixture.append(card);
      document.body.append(fixture);
    });
    const card = page.getByTestId("public-surface-fixture");
    const surface = card.getByRole("article");
    const action = card.getByRole("button", { name: "公开容器操作", exact: true });
    const readSurface = () => surface.evaluate(element => {
      const style = getComputedStyle(element);
      return { background: style.backgroundColor, image: style.backgroundImage, border: style.borderWidth,
        shadow: style.boxShadow, filter: style.filter, opacity: style.opacity };
    });
    for (const width of [360, 768, 1280]) {
      await page.setViewportSize({ width, height: 900 });
      await expect(action).toBeVisible();
      await page.mouse.move(0, 0);
      const before = await readSurface();
      expect(before.background).toBe("rgba(0, 0, 0, 0)");
      expect(before.shadow).toBe("none");
      expect(before.filter).toBe("none");
      await surface.hover();
      expect(await readSurface()).toEqual(before);
      await action.focus();
      await expect(action).toBeFocused();
      const box = await action.boundingBox();
      expect(box.height).toBeGreaterThanOrEqual(40);
      await page.mouse.move(0, 0);
      const buttonBefore = await action.evaluate(element => getComputedStyle(element).backgroundColor);
      await action.hover();
      await expect.poll(() => action.evaluate(element => getComputedStyle(element).backgroundColor)).not.toBe(buttonBefore);
      expect(await readSurface()).toEqual(before);
      expect(await card.evaluate(element => element.scrollWidth <= element.clientWidth + 1)).toBe(true);
    }
  }
});
