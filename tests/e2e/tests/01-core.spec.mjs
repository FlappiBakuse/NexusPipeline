import { test, expect } from "@playwright/test";
import { api, baseUrl, CI_MODE, createScript, ensureService, makeScriptDir } from "./helpers.mjs";

await ensureService();

test("仪表盘：统计卡片 + 版本 + 状态优先布局", async ({ page }) => {
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.waitForSelector(".stat-grid", { timeout: 15000 });
  const body = await page.textContent("body");
  expect(body.includes("脚本实例") && body.includes("调度队列"), "首行含脚本实例与调度队列统计卡片").toBeTruthy();
  expect(body.includes("当前版本"), "首行含当前版本卡片").toBeTruthy();
  // v0.6.7+：版本断言改从 /api/status 动态读取，消除发版漏改测试导致的误红
  const status = await (await fetch(baseUrl + "api/status")).json();
  expect(body.includes(status.version), `版本显示 ${status.version}（x.x.x 不带 v）`).toBeTruthy();
  expect(body.includes("下一调度队列"), "首行含下一调度队列卡片").toBeTruthy();
  const nums = await page.$$eval(".stat .num", els => els.map(e => e.textContent.trim()));
  expect(nums.includes("无"), "无定时队列时下一调度显示「无」").toBeTruthy();
  const disabledPlugins = (await (await fetch(baseUrl + "api/status")).json()).plugins?.filter(plugin => !plugin.enabled) || [];
  expect(await page.locator(".plugin-card").count(), "健康插件不占用仪表盘主视觉").toBe(0);
  expect(await page.locator("#dashboard-plugin-panel").isVisible(), "无异常时插件摘要保持收起").toBe(disabledPlugins.length > 0);
  if (disabledPlugins.length) expect(body.includes(disabledPlugins[0].displayName), "异常插件会在仪表盘提示").toBeTruthy();
  expect(await page.locator(".main-nav .nav-icon svg").count(), "主导航统一使用 SVG 图标").toBe(7);
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

test("响应式内容组合：标题、统计区和脚本/队列操作区保持完整", async ({ page }) => {
  const fixture = makeScriptDir("responsive-layout");
  let scriptId = "";
  let queueId = "";
  try {
    const created = await createScript({ name: "响应式布局脚本", rootPath: fixture.root, mainExe: fixture.main, configPath: fixture.cfg, logPath: fixture.log });
    expect(created.ok, "响应式布局测试脚本创建成功").toBeTruthy();
    scriptId = created.id;
    const queueResponse = await api("POST", "/api/queues", { name: "响应式布局队列", autoRunMode: "none", completionAction: "none", timeSets: [], tasks: [{ id: "", index: 0, scriptInstanceId: scriptId }] });
    expect(queueResponse.ok, "响应式布局测试队列创建成功").toBeTruthy();
    queueId = (await queueResponse.json()).id;

    await page.setViewportSize({ width: 360, height: 800 });
    await page.goto(baseUrl + "#/scripts", { waitUntil: "domcontentloaded" });
    await page.waitForSelector('[data-testid="script-card"]');
    const scriptLayout = await page.evaluate(() => {
      const card = document.querySelector('[data-testid="script-card"]');
      const main = card?.querySelector(".script-main");
      const ops = card?.querySelector(".script-ops");
      const buttons = Array.from(card?.querySelectorAll('.script-ops button:not([role="menuitem"])') || []);
      const cardBox = card?.getBoundingClientRect();
      const opsBox = ops?.getBoundingClientRect();
      const tops = buttons.map(button => button.getBoundingClientRect().top);
      return {
        actionCount: buttons.length,
        actionRow: !!opsBox && !!cardBox && opsBox.left >= cardBox.left - 1 && opsBox.right <= cardBox.right + 1 && opsBox.bottom <= cardBox.bottom + 1,
        buttonsInline: tops.length === 2 && Math.max(...tops) - Math.min(...tops) <= 1,
        buttonsFit: buttons.every(button => { const box = button.getBoundingClientRect(); return box.left >= (cardBox?.left || 0) - 1 && box.right <= (cardBox?.right || 0) + 1 && box.width >= 0; }),
        actionBelowCopy: !!main && !!opsBox && opsBox.top >= main.getBoundingClientRect().bottom - 1,
      };
    });
    expect(scriptLayout.actionCount, "手机脚本卡片保留两个高频操作").toBe(2);
    expect(scriptLayout.actionRow && scriptLayout.buttonsFit, "手机脚本操作区完整位于卡片内").toBeTruthy();
    expect(scriptLayout.buttonsInline && scriptLayout.actionBelowCopy, "手机脚本高频操作横向排列且位于正文之后").toBeTruthy();

    await page.goto(baseUrl + "#/queues", { waitUntil: "domcontentloaded" });
    await page.waitForSelector('[data-testid="queue-card"]');
    const queueLayout = await page.evaluate(() => {
      const card = document.querySelector('[data-testid="queue-card"]');
      const ops = card?.querySelector(".queue-ops");
      const buttons = Array.from(card?.querySelectorAll('.queue-ops button:not([role="menuitem"])') || []);
      const cardBox = card?.getBoundingClientRect();
      const opsBox = ops?.getBoundingClientRect();
      const tops = buttons.map(button => button.getBoundingClientRect().top);
      return {
        actionCount: buttons.length,
        actionRow: !!opsBox && !!cardBox && opsBox.left >= cardBox.left - 1 && opsBox.right <= cardBox.right + 1 && opsBox.bottom <= cardBox.bottom + 1,
        buttonsInline: tops.length === 2 && Math.max(...tops) - Math.min(...tops) <= 1,
      };
    });
    expect(queueLayout.actionCount, "手机队列卡片保留两个高频操作").toBe(2);
    expect(queueLayout.actionRow && queueLayout.buttonsInline, "手机队列操作区整行排列且位于卡片内").toBeTruthy();

    await page.goto(baseUrl + "#/dashboard", { waitUntil: "domcontentloaded" });
    await page.waitForSelector('[data-testid="stat-scripts"]');
    const dashboardLayout = await page.evaluate(() => {
      const grid = document.querySelector(".stat-grid-operational");
      const stats = Array.from(grid?.querySelectorAll(".stat") || []).map(item => item.getBoundingClientRect());
      const gridBox = grid?.getBoundingClientRect();
      const gaps = stats.slice(1).map((box, index) => box.top - stats[index].bottom);
      return {
        count: stats.length,
        equalWidth: stats.length === 4 && Math.max(...stats.map(box => box.width)) - Math.min(...stats.map(box => box.width)) <= 2,
        equalHeight: stats.length === 4 && Math.max(...stats.map(box => box.height)) - Math.min(...stats.map(box => box.height)) <= 1,
        uniformGap: stats.length === 4 && Math.max(...gaps) - Math.min(...gaps) <= 1,
        inside: !!gridBox && stats.every(box => box.left >= gridBox.left - 1 && box.right <= gridBox.right + 1),
      };
    });
    expect(dashboardLayout.count, "Dashboard 保留四张运行卡片").toBe(4);
    expect(dashboardLayout.equalWidth && dashboardLayout.equalHeight && dashboardLayout.uniformGap && dashboardLayout.inside, "Dashboard 四张卡片保持等宽、等高且间隔一致").toBeTruthy();
  } finally {
    if (queueId) await api("DELETE", "/api/queues/" + queueId);
    if (scriptId) await api("DELETE", "/api/scripts/" + scriptId);
  }
  await page.setViewportSize({ width: 1280, height: 900 });
});

test("验收修正：手机用户/调度布局与队列、插件细节保持一致", async ({ page }) => {
  const fixture = makeScriptDir("acceptance-layout");
  let scriptId = "";
  let queueId = "";
  try {
    const created = await createScript({ name: "验收布局脚本", rootPath: fixture.root, mainExe: fixture.main, configPath: fixture.cfg, logPath: fixture.log });
    expect(created.ok, "验收布局测试脚本创建成功").toBeTruthy();
    scriptId = created.id;
    const queueResponse = await api("POST", "/api/queues", {
      name: "验收布局队列",
      autoRunMode: "scheduled",
      completionAction: "none",
      timeSets: [{ id: "", enabled: true, days: [0, 1, 2, 3, 4, 5, 6], time: "05:30" }],
      tasks: [{ id: "", index: 0, scriptInstanceId: scriptId }],
    });
    expect(queueResponse.ok, "验收布局测试队列创建成功").toBeTruthy();
    queueId = (await queueResponse.json()).id;

    await page.setViewportSize({ width: 360, height: 800 });
    await page.goto(baseUrl + `#/scripts/${scriptId}/users`, { waitUntil: "domcontentloaded" });
    await page.waitForSelector(".user-card");
    const userLayout = await page.evaluate(() => {
      const card = document.querySelector(".user-card");
      const info = card?.querySelector(".list-item-head > div:first-child");
      const actions = card?.querySelector(".action-row");
      const handle = card?.querySelector(".drag-handle");
      const cardBox = card?.getBoundingClientRect();
      const actionBox = actions?.getBoundingClientRect();
      return {
        cardGrid: getComputedStyle(card).display === "grid",
        handleWidth: handle?.getBoundingClientRect().width || 0,
        actionsBelowInfo: !!info && !!actionBox && actionBox.top >= info.getBoundingClientRect().bottom - 1,
        actionsInside: !!cardBox && !!actionBox && actionBox.left >= cardBox.left - 1 && actionBox.right <= cardBox.right + 1,
        actionCount: actions?.querySelectorAll('button:not([role="menuitem"])').length || 0,
      };
    });
    expect(userLayout.cardGrid, "手机用户卡片采用脚本实例式网格布局").toBeTruthy();
    expect(userLayout.handleWidth >= 44, "手机用户卡片拖拽把手保持触控尺寸").toBeTruthy();
    expect(userLayout.actionsBelowInfo && userLayout.actionsInside && userLayout.actionCount === 2, "手机用户高频操作区位于正文之后且完整排列").toBeTruthy();

    await page.click('[data-action="edit-user"][data-name="默认"]');
    await page.waitForSelector(".modal .switch-row");
    const toggleLayout = await page.$$eval(".modal .switch-row", rows => rows.map(row => {
      const button = row.querySelector(".mode-toggle")?.getBoundingClientRect();
      const note = row.querySelector(".muted")?.getBoundingClientRect();
      return { buttonAfterNote: !!button && !!note && button.left >= note.left, hasStateVisual: !!row.querySelector(".switch-track") };
    }));
    expect(toggleLayout.length, "用户编辑弹窗保留三个切换项").toBe(3);
    expect(toggleLayout.every(item => item.buttonAfterNote && item.hasStateVisual), "用户编辑弹窗切换项采用右侧轨道开关布局").toBeTruthy();
    await page.click('[data-action="close-modal"]');

    await page.goto(baseUrl + "#/dispatch", { waitUntil: "domcontentloaded" });
    await page.waitForSelector("#dc-script");
    const dispatchLayout = await page.evaluate(() => {
      const bar = document.querySelector(".dispatch-runbar")?.getBoundingClientRect();
      const controls = Array.from(document.querySelectorAll(".dispatch-runbar select, .dispatch-runbar button")).filter(control => control.offsetParent !== null);
      return { bar, inside: !!bar && controls.every(control => { const box = control.getBoundingClientRect(); return box.left >= bar.left - 1 && box.right <= bar.right + 1 && box.bottom <= bar.bottom + 1; }) };
    });
    expect(dispatchLayout.inside, "手机调度中心统一执行条控件位于容器内").toBeTruthy();

    await page.goto(baseUrl + "#/queues", { waitUntil: "domcontentloaded" });
    await page.waitForSelector(`[data-action="edit-queue"][data-id="${queueId}"]`);
    await page.click(`[data-action="edit-queue"][data-id="${queueId}"]`);
    await page.waitForSelector("#qm-timesets");
    const timeSetList = await page.$eval("#qm-timesets", el => ({ className: el.className, borderWidth: getComputedStyle(el).borderWidth, borderRadius: getComputedStyle(el).borderRadius }));
    expect(timeSetList.className.includes("timeset-list") && timeSetList.borderWidth === "0px" && timeSetList.borderRadius === "0px", "定时列表整体外框已移除").toBeTruthy();
    await page.click('[data-action="close-modal"]');

    await page.goto(baseUrl + "#/plugins", { waitUntil: "domcontentloaded" });
    await page.waitForSelector(".plugins-table");
    const pluginLayout = await page.$eval(".plugins-table", table => {
      const group = table.querySelector(".plugin-group");
      const helper = table.nextElementSibling;
      const name = table.querySelector(".plugin-name-scroll");
      return {
        groupCount: table.querySelectorAll(".plugin-group").length,
        helperPadding: helper ? parseFloat(getComputedStyle(helper).paddingLeft) : 0,
        nameFocusable: name?.getAttribute("tabindex") === "0",
      };
    });
    expect(pluginLayout.groupCount > 0 && pluginLayout.helperPadding >= 0 && pluginLayout.nameFocusable, "插件按分组列表展示且名称可聚焦滚动").toBeTruthy();
  } finally {
    if (queueId) await api("DELETE", "/api/queues/" + queueId);
    if (scriptId) await api("DELETE", "/api/scripts/" + scriptId);
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
    const style = getComputedStyle(modal);
    const modalBody = document.querySelector(".modal-body");
    const modalFooter = document.querySelector(".modal-footer");
    const exe = document.querySelector("#sm-exe");
    const args = document.querySelector("#sm-args");
    const wait = document.querySelector("#sm-game-wait");
    const total = document.querySelector("#sm-total");
    const waitWidthPx = wait?.getBoundingClientRect().width || 0;
    const totalWidthPx = total?.getBoundingClientRect().width || 0;
    return { fits: modal.getBoundingClientRect().width <= window.innerWidth, stacked: args.getBoundingClientRect().top > exe.getBoundingClientRect().top, dialog: modal.getAttribute("role") === "dialog" && modal.getAttribute("aria-modal") === "true", focus: document.activeElement?.id === "sm-name", column: style.flexDirection === "column", bodyScroll: modalBody && getComputedStyle(modalBody).overflowY === "auto", footer: !!modalFooter, waitWidth: wait && total && Math.abs(waitWidthPx - totalWidthPx) <= 1, waitWidthPx, totalWidthPx };
  });
  expect(modalMetrics.fits, "手机端弹窗不超出视口").toBeTruthy();
  expect(modalMetrics.stacked, "手机端脚本表单自动堆叠").toBeTruthy();
  expect(modalMetrics.dialog, "弹窗包含可访问语义").toBeTruthy();
  expect(modalMetrics.focus, "弹窗打开后焦点进入第一个字段").toBeTruthy();
  expect(modalMetrics.column && modalMetrics.bodyScroll && modalMetrics.footer, "长表单弹窗标题/正文/操作区分层滚动").toBeTruthy();
  expect(modalMetrics.waitWidth, `手机端启动后等待秒数与运行设置输入框等宽（${modalMetrics.waitWidthPx} → ${modalMetrics.totalWidthPx}）`).toBeTruthy();
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
    if (parts.length < 2) return false;
    const tops = parts.map(x => x.getBoundingClientRect().top);
    return Math.max(...tops) - Math.min(...tops) <= 4;
  });
  expect(taskRowInline, "手机端任务列表选择器与删除按钮同一行").toBeTruthy();
  await page.click(".modal button:has-text('取消')");

  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto(baseUrl + "#/dispatch", { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#dc-script");
  const dispatchBar = await page.evaluate(() => {
    const bar = document.querySelector(".dispatch-runbar")?.getBoundingClientRect();
    const button = document.querySelector("#dc-run")?.getBoundingClientRect();
    return !!bar && !!button && button.right <= bar.right + 1 && button.width < bar.width * .35;
  });
  expect(dispatchBar, "桌面端统一执行条保持紧凑执行按钮").toBeTruthy();
  await page.setViewportSize({ width: 360, height: 800 });
  await page.goto(baseUrl + "#/dispatch", { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#dc-script");
  const barStack = await page.evaluate(() => {
    const bar = document.querySelector(".dispatch-runbar")?.getBoundingClientRect();
    const fields = Array.from(document.querySelectorAll(".dispatch-runbar .field")).filter(field => !field.hidden && field.offsetParent !== null);
    return !!bar && fields.length >= 2 && fields.every(field => { const box = field.getBoundingClientRect(); return box.left >= bar.left - 1 && box.right <= bar.right + 1; });
  });
  expect(barStack, "手机竖屏统一执行条保持堆叠且不溢出").toBeTruthy();
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
