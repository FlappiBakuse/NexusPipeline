import { test, expect } from "@playwright/test";
import { api, baseUrl, createScript, makeScriptDir, PING_GAME, waitNoRunning } from "./helpers.mjs";
import fs from "node:fs";
import path from "node:path";

async function chooseCustomSelect(page, id, value) {
  await page.locator(`#${id}-trigger`).click();
  await page.locator(`#${id}-menu [data-nxp-select-option][data-value="${value}"]`).click();
}

test("调度队列入口：创建、编辑和删除一个手动队列", async ({ page }) => {
  const suffix = Date.now();
  const fixture = makeScriptDir(`smoke-queue-${suffix}`);
  const script = await createScript({ name: `Smoke 队列脚本-${suffix}`, rootPath: fixture.root, mainExe: fixture.main, configPath: fixture.cfg, logPath: fixture.log });
  expect(script.ok).toBeTruthy();
  try {
    await page.goto(baseUrl + "#/queues", { waitUntil: "domcontentloaded" });
    await page.getByRole("button", { name: "新建调度队列", exact: true }).click();
    await page.locator("#qm-name").fill(`Smoke 队列-${suffix}`);
    await page.getByRole("button", { name: "+ 添加任务", exact: true }).click();
    await chooseCustomSelect(page, "qm-task-0", script.id);
    await page.locator(".modal").getByRole("button", { name: "保存", exact: true }).click();
    const card = page.getByTestId("queue-card").filter({ hasText: `Smoke 队列-${suffix}` }).first();
    await expect(card).toBeVisible();

    await card.locator('[data-action="edit-queue"]').click();
    await page.locator("#qm-name").fill(`Smoke 队列-已编辑-${suffix}`);
    await page.locator(".modal").getByRole("button", { name: "保存", exact: true }).click();
    await expect(page.getByTestId("queue-card").filter({ hasText: `Smoke 队列-已编辑-${suffix}` })).toBeVisible();

    await page.getByTestId("queue-card").filter({ hasText: `Smoke 队列-已编辑-${suffix}` }).first().locator('[data-action="delete-queue"]').click();
    await page.locator('[data-action="confirm-delete-queue"]').click();
    await expect(page.getByTestId("queue-card").filter({ hasText: `Smoke 队列-已编辑-${suffix}` })).toHaveCount(0);
  } finally {
    await api("DELETE", `/api/users/${encodeURIComponent(script.userId)}`, { confirmName: script.userName });
    await api("DELETE", `/api/scripts/${encodeURIComponent(script.id)}`);
  }
});

test("调度中心入口：从队列选择器启动并看到运行状态", async ({ page }) => {
  const suffix = Date.now();
  const fixture = makeScriptDir(`smoke-dispatch-${suffix}`);
  const runLog = path.join(fixture.log, "queue.log");
  fs.writeFileSync(fixture.main, `@echo off\r\nping 127.0.0.1 -n 7 >nul\r\necho queue-ok>>"${runLog}"\r\nexit /b 0\r\n`, "ascii");
  const script = await createScript({
    name: `Smoke 调度脚本-${suffix}`,
    rootPath: fixture.root,
    mainExe: fixture.main,
    configPath: fixture.cfg,
    logPath: fixture.log,
    successKeywords: "queue-ok",
    maxAttempts: 1,
  });
  const queueResponse = await api("POST", "/api/queues", {
    name: `Smoke 调度队列-${suffix}`,
    autoRunMode: "none",
    completionAction: "none",
    timeSets: [],
    tasks: [{ id: "", index: 0, scriptInstanceId: script.id }],
  });
  const queue = await queueResponse.json();
  try {
    await page.goto(baseUrl + "#/dispatch", { waitUntil: "domcontentloaded" });
    await chooseCustomSelect(page, "dc-kind", "queue");
    await chooseCustomSelect(page, "dc-queue", queue.id);
    const dispatchResponse = page.waitForResponse(response => response.url().endsWith("/api/dispatch/queue") && response.request().method() === "POST");
    await page.getByTestId("dispatch-run").click();
    expect((await dispatchResponse).ok()).toBeTruthy();
    await expect(page.getByTestId("dispatch-running")).toContainText(`Smoke 调度队列-${suffix}`, { timeout: 10000 });
    await expect.poll(async () => (await (await api("GET", "/api/status")).json()).running.length, { timeout: 60000 }).toBe(0);
    await waitNoRunning();
  } finally {
    await api("DELETE", `/api/queues/${encodeURIComponent(queue.id)}`);
    await api("DELETE", `/api/users/${encodeURIComponent(script.userId)}`, { confirmName: script.userName });
    await api("DELETE", `/api/scripts/${encodeURIComponent(script.id)}`);
  }
});
