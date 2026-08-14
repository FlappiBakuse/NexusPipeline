import { test, expect } from "@playwright/test";
import { baseUrl, CI_MODE, ensureService } from "./helpers.mjs";

await ensureService();

test("仪表盘：统计卡片 + 版本 + 插件配置信息", async ({ page }) => {
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.waitForSelector(".stat-grid", { timeout: 15000 });
  const body = await page.textContent("body");
  expect(body.includes("通知推送"), "插件「通知推送」在页面可见").toBeTruthy();
  expect(body.includes("脚本实例") && body.includes("调度队列"), "首行含脚本实例与调度队列统计卡片").toBeTruthy();
  expect(body.includes("当前版本"), "首行含当前版本卡片").toBeTruthy();
  // v0.6.7+：版本断言改从 /api/status 动态读取，消除发版漏改测试导致的误红
  const status = await (await fetch(baseUrl + "api/status")).json();
  expect(body.includes(status.version), `版本显示 ${status.version}（x.x.x 不带 v）`).toBeTruthy();
  expect(body.includes("下一调度队列"), "首行含下一调度队列卡片").toBeTruthy();
  const nums = await page.$$eval(".stat .num", els => els.map(e => e.textContent.trim()));
  expect(nums.includes("无"), "无定时队列时下一调度显示「无」").toBeTruthy();
  const pcards = await page.$$eval(".plugin-card", els => els.map(e => e.textContent.trim()));
  expect(pcards.some(t => t.includes("通知推送")), "插件小卡片含「通知推送」").toBeTruthy();
  expect(body.includes("已启用通知"), "仪表盘插件卡片显示通知配置信息").toBeTruthy();
});

test("响应式冒烟：手机 / 平板 / 电脑视口无横向溢出（粗检）", async ({ page }) => {
  const sizes = [
    { width: 360, height: 800, name: "手机" },
    { width: 768, height: 900, name: "平板" },
    { width: 1280, height: 900, name: "电脑" },
  ];
  for (const size of sizes) {
    await page.setViewportSize({ width: size.width, height: size.height });
    await page.goto(baseUrl + "#/dashboard", { waitUntil: "domcontentloaded" });
    await page.waitForSelector(".stat-grid", { timeout: 10000 });
    const noOverflow = await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1);
    expect(noOverflow, `${size.name}视口没有横向溢出（${size.width}px）`).toBeTruthy();
  }
  await page.setViewportSize({ width: 1280, height: 900 });
});

test("响应式外壳：手机 / 平板 / 电脑 + 主题 + 粒子效果", async ({ page }) => {
  test.skip(CI_MODE, "CI 模式跳过响应式外壳外观用例");
  const sizes = [
    { width: 360, height: 800, name: "手机" },
    { width: 768, height: 900, name: "平板" },
    { width: 1280, height: 900, name: "电脑" },
  ];
  for (const size of sizes) {
    await page.setViewportSize({ width: size.width, height: size.height });
    await page.goto(baseUrl + "#/dashboard", { waitUntil: "domcontentloaded" });
    await page.waitForSelector(".stat-grid", { timeout: 10000 });
    const metrics = await page.evaluate(() => ({
      noOverflow: document.documentElement.scrollWidth <= window.innerWidth + 1,
      canvas: document.querySelector("#ambient-particles")?.getAttribute("aria-hidden") === "true"
        && getComputedStyle(document.querySelector("#ambient-particles")).pointerEvents === "none",
      topbar: getComputedStyle(document.querySelector(".topbar")).display !== "none",
    }));
    expect(metrics.noOverflow, `${size.name}视口没有横向溢出（${size.width}px）`).toBeTruthy();
    expect(metrics.canvas, `${size.name}视口粒子层不拦截交互（${size.width}px）`).toBeTruthy();
    expect(metrics.topbar === (size.width <= 820), `${size.name}视口导航形态正确（${size.width}px）`).toBeTruthy();
  }

  await page.evaluate(() => localStorage.removeItem("nexus-theme"));
  await page.reload({ waitUntil: "domcontentloaded" });
  await page.waitForSelector(".stat-grid");
  await page.locator('[data-action="toggle-theme"]:visible').click();
  const lightTheme = await page.evaluate(() => document.body.dataset.theme);
  expect(lightTheme === "light", "主题切换可进入浅色模式").toBeTruthy();
  await page.locator('[data-action="toggle-theme"]:visible').click();
  const darkTheme = await page.evaluate(() => document.body.dataset.theme);
  expect(darkTheme === "dark", "主题切换可进入深色模式").toBeTruthy();
  await page.locator('[data-action="toggle-theme"]:visible').click();

  await page.emulateMedia({ reducedMotion: "reduce" });
  await page.reload({ waitUntil: "domcontentloaded" });
  await page.waitForSelector(".stat-grid");
  expect(await page.evaluate(() => getComputedStyle(document.querySelector("#ambient-particles")).display !== "none" && document.querySelector("#ambient-particles").dataset.ready === "true"), "减少动画模式保留静态粒子").toBeTruthy();
  await page.emulateMedia({ reducedMotion: "no-preference" });

  await page.setViewportSize({ width: 360, height: 800 });
  await page.click('[data-action="open-nav"]');
  expect(await page.evaluate(() => document.body.classList.contains("nav-open")), "手机端可以打开导航抽屉").toBeTruthy();
  await page.click('.nav-backdrop', { position: { x: 340, y: 200 } });
  expect(!(await page.evaluate(() => document.body.classList.contains("nav-open"))), "手机端可以关闭导航抽屉").toBeTruthy();

  await page.goto(baseUrl + "#/scripts", { waitUntil: "domcontentloaded" });
  await page.waitForSelector("h2");
  await page.click('[data-testid="new-script"]');
  await page.waitForSelector(".new-script-chooser", { timeout: 5000 });
  const chooserStack = await page.evaluate(() => {
    const cards = Array.from(document.querySelectorAll(".chooser-card"));
    if (cards.length < 4) return false;
    for (let i = 1; i < cards.length; i++) {
      const prev = cards[i - 1].getBoundingClientRect();
      const cur = cards[i].getBoundingClientRect();
      if (!(cur.top > prev.bottom) || Math.abs(cur.left - prev.left) > 1) return false;
    }
    return true;
  });
  expect(chooserStack, "手机端新建选择卡片堆叠").toBeTruthy();
  await page.click('[data-action="open-script-type"][data-plugin=""]');
  await page.waitForSelector(".modal");
  await page.waitForFunction(() => document.activeElement?.id === "sm-name", null, { timeout: 2000 });
  const modalMetrics = await page.evaluate(() => {
    const modal = document.querySelector(".modal");
    const exe = document.querySelector("#sm-exe");
    const args = document.querySelector("#sm-args");
    return { fits: modal.getBoundingClientRect().width <= window.innerWidth, stacked: args.getBoundingClientRect().top > exe.getBoundingClientRect().top, dialog: modal.getAttribute("role") === "dialog" && modal.getAttribute("aria-modal") === "true", focus: document.activeElement?.id === "sm-name" };
  });
  expect(modalMetrics.fits, "手机端弹窗不超出视口").toBeTruthy();
  expect(modalMetrics.stacked, "手机端脚本表单自动堆叠").toBeTruthy();
  expect(modalMetrics.dialog, "弹窗包含可访问语义").toBeTruthy();
  expect(modalMetrics.focus, "弹窗打开后焦点进入第一个字段").toBeTruthy();
  await page.click('[data-action="close-modal"]');

  await page.goto(baseUrl + "#/queues", { waitUntil: "domcontentloaded" });
  await page.waitForSelector("h2");
  await page.click("button:has-text('新建调度队列')");
  await page.waitForSelector("#qm-name");
  await page.click("text=+ 添加任务");
  await page.waitForSelector(".task-row");
  const taskRowInline = await page.evaluate(() => {
    const row = document.querySelector(".task-row");
    if (!row) return false;
    const parts = Array.from(row.querySelectorAll("select, button"));
    if (parts.length < 4) return false;
    const tops = parts.map(x => x.getBoundingClientRect().top);
    return Math.max(...tops) - Math.min(...tops) <= 4;
  });
  expect(taskRowInline, "手机端任务列表选择器与上移/下移/删除按钮同一行").toBeTruthy();
  await page.click(".modal button:has-text('取消')");

  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto(baseUrl + "#/dispatch", { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#dc-script");
  const dispatchButtons = await page.evaluate(() => Array.from(document.querySelectorAll(".control-action button")).map(button => ({ width: button.getBoundingClientRect().width, card: button.closest(".card").getBoundingClientRect().width })));
  expect(dispatchButtons.length === 2 && dispatchButtons.every(item => item.width / item.card <= 0.25), "调度中心执行按钮保持紧凑宽度").toBeTruthy();
  const cardRow = await page.evaluate(() => {
    const cards = Array.from(document.querySelectorAll(".dispatch-cards > .card"));
    if (cards.length !== 2) return false;
    const a = cards[0].getBoundingClientRect();
    const b = cards[1].getBoundingClientRect();
    return a.right <= b.left + 1 && Math.abs(a.top - b.top) <= 1;
  });
  expect(cardRow, "桌面端脚本/队列执行卡片同排").toBeTruthy();
  await page.setViewportSize({ width: 360, height: 800 });
  await page.goto(baseUrl + "#/dispatch", { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#dc-script");
  const cardStack = await page.evaluate(() => {
    const cards = Array.from(document.querySelectorAll(".dispatch-cards > .card"));
    if (cards.length !== 2) return false;
    const a = cards[0].getBoundingClientRect();
    const b = cards[1].getBoundingClientRect();
    return a.bottom <= b.top + 1 && Math.abs(a.left - b.left) <= 1;
  });
  expect(cardStack, "手机竖屏脚本/队列执行卡片保持堆叠").toBeTruthy();
  await page.setViewportSize({ width: 1280, height: 900 });
});

test("菜单切换：无回弹", async ({ page }) => {
  await page.goto(baseUrl + "#/scripts", { waitUntil: "domcontentloaded" });
  await page.waitForSelector("h2");
  const pages = {
    scripts: "脚本实例", queues: "调度队列", dispatch: "调度中心",
    history: "历史记录", plugins: "插件", settings: "设置", dashboard: "仪表盘",
  };
  for (const [hash, title] of Object.entries(pages)) {
    await page.click('nav a[href="#/' + hash + '"]');
    await page.waitForFunction(t => {
      const h2 = document.querySelector("h2");
      return h2 && h2.textContent.includes(t);
    }, title, { timeout: 5000 });
    const h2 = await page.textContent("h2");
    expect(h2.includes(title), "页面「" + title + "」正常打开（h2=" + h2.trim() + "）").toBeTruthy();
  }
  await page.click('nav a[href="#/scripts"]');
  await page.waitForTimeout(3600);
  const h2 = await page.textContent("h2");
  expect(h2.includes("脚本实例"), "停留在脚本实例页 3.5 秒后未被仪表盘轮询覆盖（回弹已修复）").toBeTruthy();
});
