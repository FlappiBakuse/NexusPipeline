import { describe, expect, it } from "vitest";
import {
  buildPluginSearchIndex,
  createPluginViewState,
  defaultPluginViewState,
  filterAndSortPlugins,
  isPluginViewStateActive,
  matchesPluginQuery,
} from "./plugin-list";

describe("plugin list search and ordering", () => {
  it("uses separate defaults for local and repository tabs", () => {
    expect(defaultPluginViewState("local")).toMatchObject({ sortBy: "name", direction: "asc" });
    expect(defaultPluginViewState("store")).toMatchObject({ sortBy: "updatedAt", direction: "desc" });
    expect(createPluginViewState({ sortBy: "unknown", direction: "unknown" }, "store"))
      .toMatchObject({ sortBy: "updatedAt", direction: "desc" });
    expect(isPluginViewStateActive(defaultPluginViewState("store"), "store")).toBe(false);
  });

  it("puts missing and invalid dates last and breaks date ties by canonical name", () => {
    const records = [
      { name: "z", updatedAt: "2026-09-29" },
      { name: "bad", updatedAt: "2026-02-30" },
      { name: "a", updatedAt: "2026-09-29" },
      { name: "old", updatedAt: "2026-08-01" },
      { name: "missing" },
    ];
    expect(filterAndSortPlugins(records, defaultPluginViewState("store"), "store").map(item => item.name))
      .toEqual(["a", "z", "old", "bad", "missing"]);
  });

  it("matches full pinyin, initials, case, separators and original names", () => {
    const plugin = { name: "zenless", displayName: "Zenless Zone Zero", gameName: "绝区零" };
    for (const query of ["juequling", "JQL", "jue qu ling", "jue-qu-ling", "绝区零", "ZENLESS"])
      expect(matchesPluginQuery(plugin, query)).toBe(true);
    expect(matchesPluginQuery(plugin, "not-a-game")).toBe(false);
    expect(matchesPluginQuery(plugin, "")).toBe(true);
  });

  it("indexes localized names without mixing romanized fields", () => {
    const plugin = {
      name: "local-name",
      displayName: "Game",
      locales: { "zh-CN": { displayName: "绝区零", gameName: "星穹铁道" } },
    };
    expect(matchesPluginQuery(plugin, "jql")).toBe(true);
    expect(matchesPluginQuery(plugin, "xqtd")).toBe(true);
    expect(matchesPluginQuery(plugin, "juequlingxingqiong")).toBe(false);
    const first = buildPluginSearchIndex(plugin);
    expect(buildPluginSearchIndex(plugin)).toBe(first);
    plugin.locales["zh-CN"].gameName = "原神";
    expect(buildPluginSearchIndex(plugin)).not.toBe(first);
    expect(matchesPluginQuery(plugin, "yuanshen")).toBe(true);
  });
});
