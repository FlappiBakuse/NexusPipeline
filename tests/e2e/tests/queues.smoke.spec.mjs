import { test, expect } from "@playwright/test";
import { api, baseUrl, createScript, makeScriptDir, PING_GAME, waitNoRunning } from "./helpers.mjs";
import fs from "node:fs";
import path from "node:path";

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
    await page.locator('[data-task-idx="0"]').selectOption(script.id);
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
    await page.selectOption("#dc-kind", "queue");
    await page.selectOption("#dc-queue", queue.id);
    const dispatchResponse = page.waitForResponse(response => response.url().endsWith("/api/dispatch/queue") && response.request().method() === "POST");
    await page.getByTestId("dispatch-run").click();
    expect((await dispatchResponse).ok()).toBeTruthy();
    await expect(page.getByTestId("dispatch-running")).toContainText(`Smoke 调度队列-${suffix}`, { timeout: 10000 });
    await expect.poll(async () => (await (await api("GET", "/api/status")).json()).running.length, { timeout: 60000 }).toBe(0);
    await waitNoRunning();
  } finally {
    await api("DELETE", `/api/queues/${encodeURIComponent(queue.id)}`);
    await api("DELETE", `/api/scripts/${encodeURIComponent(script.id)}`);
  }
});

test("队列编辑表单：星期周期和任务列表可以打开", async ({ page }) => {
  const fixture = makeScriptDir("smoke-queue-form");
  const script = await createScript({ name: "Smoke 队列表单脚本", rootPath: fixture.root, mainExe: fixture.main, configPath: fixture.cfg, logPath: fixture.log });
  const response = await api("POST", "/api/queues", {
    name: "Smoke 队列表单",
    autoRunMode: "scheduled",
    completionAction: "none",
    timeSets: [{ id: "", enabled: true, days: [1, 2, 3, 4, 5], time: "08:00" }],
    tasks: [{ id: "", index: 0, scriptInstanceId: script.id }],
  });
  const queue = await response.json();
  try {
    await page.goto(baseUrl + "#/queues", { waitUntil: "domcontentloaded" });
    await page.getByTestId("queue-card").filter({ hasText: "Smoke 队列表单" }).locator('[data-action="edit-queue"]').click();
    await expect(page.locator("#qm-timesets")).toBeVisible();
    await expect(page.locator("#qm-timesets [data-ts-days]")).toHaveCount(7);
    await expect(page.locator("#qm-tasks")).toBeVisible();
    await page.locator(".modal").getByRole("button", { name: "取消", exact: true }).click();
  } finally {
    await api("DELETE", `/api/queues/${encodeURIComponent(queue.id)}`);
    await api("DELETE", `/api/scripts/${encodeURIComponent(script.id)}`);
  }
});

test("调度中心入口：运行中任务可以取消", async ({ page }) => {
  const suffix = Date.now();
  const fixture = makeScriptDir(`smoke-cancel-${suffix}`);
  fs.writeFileSync(fixture.main, "@echo off\r\nping 127.0.0.1 -n 20 >nul\r\nexit /b 0\r\n", "ascii");
  const script = await createScript({ name: `Smoke 取消脚本-${suffix}`, rootPath: fixture.root, mainExe: fixture.main, configPath: fixture.cfg, logPath: fixture.log, maxAttempts: 1 });
  const queueResponse = await api("POST", "/api/queues", {
    name: `Smoke 取消队列-${suffix}`,
    autoRunMode: "none",
    completionAction: "none",
    timeSets: [],
    tasks: [{ id: "", index: 0, scriptInstanceId: script.id }],
  });
  const queue = await queueResponse.json();
  try {
    await page.goto(baseUrl + "#/dispatch", { waitUntil: "domcontentloaded" });
    await page.selectOption("#dc-kind", "queue");
    await page.selectOption("#dc-queue", queue.id);
    await page.getByTestId("dispatch-run").click();
    const running = page.getByTestId("dispatch-running");
    await expect(running).toContainText(`Smoke 取消队列-${suffix}`, { timeout: 10000 });
    await running.locator('[data-action="cancel-run"]').click();
    await expect.poll(async () => (await (await api("GET", "/api/status")).json()).running.length, { timeout: 30000 }).toBe(0);
  } finally {
    await api("DELETE", `/api/queues/${encodeURIComponent(queue.id)}`);
    await api("DELETE", `/api/scripts/${encodeURIComponent(script.id)}`);
  }
});
