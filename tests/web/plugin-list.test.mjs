import test from "node:test";
import assert from "node:assert/strict";
import { filterAndSortPlugins } from "../../wwwroot/core/plugin-list.js";

const plugins = [
  { name: "alpha", displayName: "阿尔法", kind: "managed-code", createdAt: "2026-08-01", updatedAt: "2026-09-01" },
  { name: "beta", displayName: "明日方舟", kind: "data-specialized", createdAt: "2026-07-01", updatedAt: "2026-08-20" },
  { name: "gamma", displayName: "原神签到", kind: "data-specialized", createdAt: "", updatedAt: "2026-09-03" },
  { name: "delta", displayName: "崩坏：星穹铁道", kind: "managed-code", createdAt: "2026-06-01", updatedAt: "" },
];

test("plugin list filters by kind and search text", () => {
  assert.deepEqual(
    filterAndSortPlugins(plugins, { kind: "data-specialized", query: "签到" }).map(plugin => plugin.name),
    ["gamma"],
  );
});

test("plugin list sorts Chinese names by pinyin in both directions", () => {
  assert.deepEqual(
    filterAndSortPlugins(plugins, { sortBy: "name", direction: "asc" }).map(plugin => plugin.name),
    ["alpha", "delta", "beta", "gamma"],
  );
  assert.deepEqual(
    filterAndSortPlugins(plugins, { sortBy: "name", direction: "desc" }).map(plugin => plugin.name),
    ["gamma", "beta", "delta", "alpha"],
  );
});

test("plugin list sorts creation and update dates while keeping missing dates last", () => {
  assert.deepEqual(
    filterAndSortPlugins(plugins, { sortBy: "createdAt", direction: "asc" }).map(plugin => plugin.name),
    ["delta", "beta", "alpha", "gamma"],
  );
  assert.deepEqual(
    filterAndSortPlugins(plugins, { sortBy: "createdAt", direction: "desc" }).map(plugin => plugin.name),
    ["alpha", "beta", "delta", "gamma"],
  );
  assert.deepEqual(
    filterAndSortPlugins(plugins, { sortBy: "updatedAt", direction: "desc" }).map(plugin => plugin.name),
    ["gamma", "alpha", "beta", "delta"],
  );
});
