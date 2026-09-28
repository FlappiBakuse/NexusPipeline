import { test, expect } from "@playwright/test";
import { api, baseUrl, createScript, makeScriptDir, PING_GAME } from "./helpers.mjs";

test("脚本入口：创建、编辑和删除一个普通脚本", async ({ page }) => {
  const fixture = makeScriptDir("smoke-script-crud");
  let createdId = "";
  try {
    await page.goto(baseUrl + "#/scripts", { waitUntil: "domcontentloaded" });
    await page.getByTestId("new-script").click();
    let modal = page.locator(".nxp-modal");
    await expect(modal).toBeVisible();
    const genericChooser = modal.getByRole("button", { name: /^通用脚本/ });
    if (await genericChooser.count()) {
      await genericChooser.click();
      modal = page.locator(".nxp-modal");
      await expect(modal.locator("#sm-mode-btn")).toBeVisible();
    }
    const judgeMode = modal.locator("#sm-mode-btn");
    await judgeMode.click();
    const judgeUpload = modal.locator("#sm-upload-btn");
    await expect(judgeUpload).toBeVisible();
    await judgeMode.click();
    await modal.locator("#sm-name").fill("Smoke 普通脚本");
    await modal.locator("#sm-root").fill(fixture.root);
    await modal.locator("#sm-exe").fill(fixture.main);
    await modal.locator("#sm-config").fill(fixture.cfg);
    await modal.locator("#sm-log").fill(fixture.log);
    await modal.locator("#sm-game-exe").fill(PING_GAME);
    await modal.getByRole("button", { name: "保存", exact: true }).click();
    await expect(modal).toBeHidden();
    const created = (await (await api("GET", "/api/scripts")).json()).find(item => item.name === "Smoke 普通脚本");
    createdId = created?.id || "";
    await expect(page.getByTestId("script-card").filter({ hasText: "Smoke 普通脚本" })).toBeVisible();

    const card = page.getByTestId("script-card").filter({ hasText: "Smoke 普通脚本" }).first();
    await card.getByRole("button", { name: "编辑脚本", exact: true }).click();
    await expect(page.locator("#sm-name")).toHaveValue("Smoke 普通脚本");
    await page.locator("#sm-name").fill("Smoke 普通脚本-已编辑");
    await page.locator(".nxp-modal").getByRole("button", { name: "保存", exact: true }).click();
    await expect(page.getByTestId("script-card").filter({ hasText: "Smoke 普通脚本-已编辑" })).toBeVisible();

    await page.getByTestId("script-card").filter({ hasText: "Smoke 普通脚本-已编辑" }).first().locator('[data-action="delete-script"]').click();
    await page.locator('[data-action="confirm-delete-script"]').click();
    await expect(page.getByTestId("script-card").filter({ hasText: "Smoke 普通脚本-已编辑" })).toHaveCount(0);
    createdId = "";
  } finally {
    if (createdId) await api("DELETE", `/api/scripts/${encodeURIComponent(createdId)}`);
  }
});

