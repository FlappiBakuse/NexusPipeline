import test from "node:test";
import assert from "node:assert/strict";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { findTopics, loadMap, validateMap } from "../../tools/docs-index.mjs";

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");

test("current documentation map validates files, code paths, authorities and test domains", () => {
  const map = loadMap(ROOT);
  const result = validateMap(ROOT, map);
  assert.deepEqual(result, { ok: true, errors: [] });
  assert.equal(new Set(map.topics.map(topic => topic.id)).size, map.topics.length);
});

test("task keywords route to current authority with code and test directions", () => {
  const map = loadMap(ROOT);
  const results = findTopics(map, "配置恢复");
  assert.ok(results.some(topic => topic.id === "architecture.configuration"));
  const topic = results.find(item => item.id === "architecture.configuration");
  assert.ok(topic.codePaths.includes("src/Services/ConfigSwapPrimitives.cs"));
  assert.ok(topic.testDomains.includes("systemGroups.config"));
});

test("historical topics stay out of default search", () => {
  const map = {
    schemaVersion: 1,
    topics: [
      { id: "current", path: "docs/current.md", summary: "当前", topics: ["配置"], aliases: [], codePaths: ["src"], testDomains: ["hostAreas.core"], authorityFor: ["current"], status: "current" },
      { id: "old", path: "docs/old.md", summary: "历史配置", topics: ["配置"], aliases: [], codePaths: ["src"], testDomains: ["hostAreas.core"], authorityFor: ["old"], status: "historical" },
    ],
  };
  assert.deepEqual(findTopics(map, "配置").map(topic => topic.id), ["current"]);
  assert.deepEqual(findTopics(map, "配置", { includeHistorical: true }).map(topic => topic.id), ["current", "old"]);
});

test("duplicate authorities and missing code paths are rejected", () => {
  const map = {
    schemaVersion: 1,
    topics: [
      { id: "a", path: "docs/README.md", summary: "a", topics: [], aliases: [], codePaths: ["src"], testDomains: ["hostAreas.core"], authorityFor: ["same"], status: "current" },
      { id: "b", path: "docs/README.md", summary: "b", topics: [], aliases: [], codePaths: ["missing"], testDomains: ["unknown"], authorityFor: ["same"], status: "current" },
    ],
  };
  const result = validateMap(ROOT, map);
  assert.equal(result.ok, false);
  assert.ok(result.errors.some(error => error.includes("authorityFor 重复")));
  assert.ok(result.errors.some(error => error.includes("codePath 不存在")));
  assert.ok(result.errors.some(error => error.includes("未知测试域")));
});
