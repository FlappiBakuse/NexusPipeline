import { describe, expect, it } from "vitest";
import { filterAndSortPlugins } from "./plugin-list";

const plugins = [
  { name: "alpha", displayName: "阿尔法", kind: "managed-code", createdAt: "2026-08-01", updatedAt: "2026-09-01" },
  { name: "beta", displayName: "明日方舟", kind: "data-specialized", createdAt: "2026-07-01", updatedAt: "2026-08-20" },
  { name: "gamma", displayName: "原神签到", kind: "data-specialized", createdAt: "", updatedAt: "2026-09-03" },
  { name: "delta", displayName: "崩坏：星穹铁道", kind: "managed-code", createdAt: "2026-06-01", updatedAt: "" },
];

describe("plugin browser filtering and sorting", () => {
  it("filters by kind and search text", () => {
    expect(
      filterAndSortPlugins(plugins, { kind: "data-specialized", query: "签到" }).map(plugin => plugin.name),
    ).toEqual(["gamma"]);
  });

  it("sorts Chinese names by pinyin in both directions", () => {
    expect(
      filterAndSortPlugins(plugins, { sortBy: "name", direction: "asc" }).map(plugin => plugin.name),
    ).toEqual(["alpha", "delta", "beta", "gamma"]);
    expect(
      filterAndSortPlugins(plugins, { sortBy: "name", direction: "desc" }).map(plugin => plugin.name),
    ).toEqual(["gamma", "beta", "delta", "alpha"]);
  });

  it("sorts creation and update dates while keeping missing dates last", () => {
    expect(
      filterAndSortPlugins(plugins, { sortBy: "createdAt", direction: "asc" }).map(plugin => plugin.name),
    ).toEqual(["delta", "beta", "alpha", "gamma"]);
    expect(
      filterAndSortPlugins(plugins, { sortBy: "createdAt", direction: "desc" }).map(plugin => plugin.name),
    ).toEqual(["alpha", "beta", "delta", "gamma"]);
    expect(
      filterAndSortPlugins(plugins, { sortBy: "updatedAt", direction: "desc" }).map(plugin => plugin.name),
    ).toEqual(["gamma", "alpha", "beta", "delta"]);
  });
});
