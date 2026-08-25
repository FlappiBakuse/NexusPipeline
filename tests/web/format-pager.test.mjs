import test from "node:test";
import assert from "node:assert/strict";
import {
  esc,
  finalStatusOf,
  fmtTime,
  statusBadge,
} from "../../wwwroot/core/format.js";
import {
  pagerMarkup,
  replacePageOrder,
} from "../../wwwroot/core/pager.js";

test("esc escapes all HTML-sensitive characters", () => {
  assert.equal(esc(`<tag a=\"1\">it's & ok</tag>`), "&lt;tag a=&quot;1&quot;&gt;it&#39;s &amp; ok&lt;/tag&gt;");
  assert.equal(esc(null), "");
});

test("statusBadge maps public status values and has a failure fallback", () => {
  assert.match(statusBadge("success"), /badge ok/);
  assert.match(statusBadge("partial"), /部分失败/);
  assert.match(statusBadge("running"), /运行中/);
  assert.match(statusBadge("cancelled"), /已取消/);
  assert.match(statusBadge("unknown"), /失败/);
});

test("finalStatusOf prefers immutable finalStatus and falls back to status", () => {
  assert.equal(finalStatusOf({ finalStatus: "partial", status: "success" }), "partial");
  assert.equal(finalStatusOf({ status: "running" }), "running");
});

test("fmtTime handles empty values without consulting the browser", () => {
  assert.equal(fmtTime(""), "-");
  assert.match(fmtTime("2026-08-25T12:34:56+08:00"), /2026/);
});

test("pagerMarkup hides the pager when all rows fit on one page", () => {
  assert.equal(pagerMarkup("scripts", 1, 20, 20), "");
});

test("pagerMarkup exposes stable page state and accessible current page", () => {
  const markup = pagerMarkup("scripts", 2, 20, 45);
  assert.match(markup, /data-testid="pager-scripts"/);
  assert.match(markup, /data-page-current="2"/);
  assert.match(markup, /data-pages="3"/);
  assert.match(markup, /共 45 条，第 21-40 条/);
  assert.match(markup, /data-page="2"[^>]*aria-current="page"/);
  assert.match(markup, /data-action="pager-prev"/);
  assert.match(markup, /data-action="pager-next"/);
});

test("replacePageOrder changes only the selected page", () => {
  const items = [
    { id: "a" }, { id: "b" }, { id: "c" },
    { id: "d" }, { id: "e" }, { id: "f" },
    { id: "g" },
  ];

  const result = replacePageOrder(items, 2, 3, ["f", "d", "e"]);
  assert.deepEqual(result.map(item => item.id), ["a", "b", "c", "f", "d", "e", "g"]);
});

test("replacePageOrder rejects incomplete or foreign page keys", () => {
  const items = [{ id: "a" }, { id: "b" }, { id: "c" }];
  assert.deepEqual(replacePageOrder(items, 1, 2, ["a"]), items);
  assert.deepEqual(replacePageOrder(items, 1, 2, ["a", "missing"]), items);
});
