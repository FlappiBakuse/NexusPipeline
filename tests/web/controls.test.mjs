import test from "node:test";
import assert from "node:assert/strict";
import {
  colorControlMarkup,
  fileControlMarkup,
  numberControlMarkup,
  rangeControlMarkup,
  selectControlMarkup,
  timeControlMarkup,
} from "../../wwwroot/core/controls.js";

test("custom select keeps a hidden value carrier and removes the native select", () => {
  const markup = selectControlMarkup(
    "queue",
    "default",
    [
      { value: "default", label: "默认队列" },
      { value: "night", label: "夜间队列" },
    ],
    'data-action="choose-queue"',
    "选择队列",
  );

  assert.match(markup, /data-nxp-select/);
  assert.match(markup, /type="hidden"[^>]*data-nxp-select-value/);
  assert.match(markup, /role="listbox"/);
  assert.match(markup, /data-nxp-select-option/);
  assert.match(markup, /aria-label="选择队列"/);
  assert.doesNotMatch(markup, /<select\b|<option\b/u);
});

test("custom number and time controls use text/button surfaces", () => {
  const number = numberControlMarkup("retry", 2, 'min="0" max="5" step="1"', "重试次数");
  const time = timeControlMarkup("start", "08:30", "", "开始时间");

  assert.match(number, /type="text"[^>]*inputmode="decimal"/);
  assert.match(number, /data-nxp-step="increment"/);
  assert.match(number, /data-nxp-step="decrement"/);
  assert.doesNotMatch(number, /type="number"/u);

  assert.match(time, /data-nxp-time/);
  assert.match(time, /data-nxp-time-hour="08"/);
  assert.match(time, /data-nxp-time-minute="30"/);
  assert.match(time, /data-nxp-time-wheels/);
  assert.match(time, /data-nxp-time-adjust="hour:-1"/);
  assert.match(time, /data-nxp-time-adjust="minute:1"/);
  assert.doesNotMatch(time, /data-nxp-time-clear|data-nxp-time-done|nxp-time-footer/u);
  assert.doesNotMatch(time, /type="time"/u);
});

test("file, color, and range controls expose only approved native carriers", () => {
  const file = fileControlMarkup("wallpaper", "", "image/*", false, "选择壁纸");
  const color = colorControlMarkup("accent", "#abc");
  const range = rangeControlMarkup("opacity", 0.8, 'min="0" max="1" step="0.05"', "卡片透明度");

  assert.match(file, /data-nxp-file-trigger/);
  assert.match(file, /class="sr-only" type="file"/);
  assert.match(file, /accept="image\/\*"/);
  assert.match(color, /data-nxp-color-trigger/);
  assert.match(color, /class="sr-only" type="color"/);
  assert.match(color, /data-nxp-color-value="#aabbcc"/);
  assert.match(range, /class="nxp-range" type="range"/);
  assert.match(range, /data-nxp-range/);
});
