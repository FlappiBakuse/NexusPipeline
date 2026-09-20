import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { findTopics, loadMap, validateCrossRepositoryLinks, validateMap } from "../../tools/docs-index.mjs";
import { findMarkdownLinks } from "../../tools/markdown.mjs";

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
  assert.ok(topic.codePaths.includes("src/Modules/Configuration/Exchange/ConfigSwapPrimitives.cs"));
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

test("cross-repository links resolve fixed historical objects and source line anchors", () => {
  const fixtureRoot = fs.mkdtempSync(path.join(os.tmpdir(), "nxp-docs-cross-repo-history-"));
  const checkout = path.join(fixtureRoot, "NexusPipeline-Plugins");
  fs.mkdirSync(path.join(checkout, "docs"), { recursive: true });
  fs.mkdirSync(path.join(checkout, "src"), { recursive: true });
  const runGit = (args) => {
    const result = spawnSync("git", args, { cwd: checkout, encoding: "utf8", windowsHide: true });
    assert.equal(result.status, 0, `git ${args.join(" ")} failed: ${result.stderr}`);
    return String(result.stdout || "").trim();
  };

  try {
    runGit(["init", "-q"]);
    runGit(["config", "user.name", "NexusPipeline docs test"]);
    runGit(["config", "user.email", "docs-test@example.invalid"]);
    fs.writeFileSync(path.join(checkout, "docs/guide.md"), "# 历史标题\n\n旧版说明。\n", "utf8");
    fs.writeFileSync(path.join(checkout, "src/example.cs"), "第一行\n第二行\n第三行\n", "utf8");
    runGit(["add", "."]);
    runGit(["commit", "-qm", "历史对象"]);
    const historicalSha = runGit(["rev-parse", "HEAD"]);
    runGit(["update-ref", "refs/remotes/origin/docs-baseline", historicalSha]);

    fs.writeFileSync(path.join(checkout, "docs/guide.md"), "# 当前标题\n\n当前说明。\n", "utf8");
    runGit(["add", "."]);
    runGit(["commit", "-qm", "当前对象"]);
    runGit(["checkout", "--detach"]);

    const remoteResult = validateCrossRepositoryLinks([{
      file: "remote-fixture.md",
      text: "[远端分支](https://github.com/FlappiBakuse/NexusPipeline-Plugins/blob/docs-baseline/docs/guide.md#历史标题)",
    }], { root: fixtureRoot, workspaceRoot: fixtureRoot });
    assert.deepEqual(remoteResult.issues, []);
    assert.equal(remoteResult.checked[0].resolvedSha, historicalSha);
    assert.notEqual(runGit(["rev-parse", "HEAD"]), historicalSha);

    const result = validateCrossRepositoryLinks([
      {
        file: "fixture.md",
        text: [
          `[历史文档](https://github.com/FlappiBakuse/NexusPipeline-Plugins/blob/${historicalSha}/docs/guide.md#历史标题)`,
          `[源码行](https://github.com/FlappiBakuse/NexusPipeline-Plugins/blob/${historicalSha}/src/example.cs#L1-L2)`,
          `[目录](https://github.com/FlappiBakuse/NexusPipeline-Plugins/tree/${historicalSha}/docs)`,
        ].join("\n"),
      },
    ], { root: fixtureRoot, workspaceRoot: fixtureRoot });

    assert.deepEqual(result.issues, []);
    assert.equal(result.checked.length, 3);
    assert.ok(result.checked.every(item => item.resolvedSha === historicalSha));
    assert.equal(result.checked.find(item => item.fragment === "L1-L2")?.path, "src/example.cs");
  } finally {
    fs.rmSync(fixtureRoot, { recursive: true, force: true });
  }
});

test("cross-repository history failures do not fall back to the candidate checkout", () => {
  const fixtureRoot = fs.mkdtempSync(path.join(os.tmpdir(), "nxp-docs-cross-repo-no-fallback-"));
  const checkout = path.join(fixtureRoot, "NexusPipeline-Plugins");
  fs.mkdirSync(path.join(checkout, "docs"), { recursive: true });
  const runGit = (args) => {
    const result = spawnSync("git", args, { cwd: checkout, encoding: "utf8", windowsHide: true });
    assert.equal(result.status, 0, `git ${args.join(" ")} failed: ${result.stderr}`);
    return String(result.stdout || "").trim();
  };

  try {
    runGit(["init", "-q"]);
    runGit(["config", "user.name", "NexusPipeline docs test"]);
    runGit(["config", "user.email", "docs-test@example.invalid"]);
    fs.writeFileSync(path.join(checkout, "docs/guide.md"), "# 当前标题\n", "utf8");
    runGit(["add", "."]);
    runGit(["commit", "-qm", "当前对象"]);
    const missingSha = "0123456789012345678901234567890123456789";
    const currentSha = runGit(["rev-parse", "HEAD"]);
    const result = validateCrossRepositoryLinks([
      {
        file: "fixture.md",
        text: [
          `[历史文档](https://github.com/FlappiBakuse/NexusPipeline-Plugins/blob/${missingSha}/docs/guide.md#历史标题)`,
          `[大小写路径](https://github.com/FlappiBakuse/NexusPipeline-Plugins/blob/${currentSha}/Docs/guide.md)`,
        ].join("\n"),
      },
    ], { root: fixtureRoot, workspaceRoot: fixtureRoot });

    assert.equal(result.ok, false);
    assert.equal(result.checked.length, 0);
    assert.ok(result.issues.some(issue => issue.includes("无法解析") && issue.includes(missingSha)));
    assert.ok(result.issues.some(issue => issue.includes("目标不存在") && issue.includes("Docs/guide.md")));
  } finally {
    fs.rmSync(fixtureRoot, { recursive: true, force: true });
  }
});

test("Markdown link extraction ignores fake links inside fenced code", () => {
  const links = findMarkdownLinks([
    "```md",
    "[假链接](https://github.com/FlappiBakuse/NexusPipeline-Plugins/blob/main/missing.md)",
    "```",
    "[真实链接](https://github.com/FlappiBakuse/NexusPipeline-Plugins/blob/main/docs/README.md)",
  ].join("\n"));
  assert.deepEqual(links.map(link => link.rawTarget), [
    "https://github.com/FlappiBakuse/NexusPipeline-Plugins/blob/main/docs/README.md",
  ]);
});
