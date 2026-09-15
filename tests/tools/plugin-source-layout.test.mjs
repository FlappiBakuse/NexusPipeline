import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..");
const pluginRepositoryCandidates = [
  path.join(repoRoot, "NexusPipeline-Plugins"),
  path.resolve(repoRoot, "..", "NexusPipeline-Plugins"),
];

function findPluginRepository() {
  const repository = pluginRepositoryCandidates.find(candidate => fs.existsSync(path.join(candidate, "catalog.json")));
  assert.ok(repository, `未找到官方插件仓库 catalog.json，已检查：${pluginRepositoryCandidates.join(", ")}`);
  return repository;
}

function sourceCategory(kind) {
  if (kind === "managed-code") return "general";
  if (kind === "data-specialized") return "specialized";
  assert.fail(`catalog 中出现未支持的插件类型：${kind}`);
}

test("官方插件 catalog 的 README 路径与源码目录布局一致", () => {
  const repository = findPluginRepository();
  const catalog = JSON.parse(fs.readFileSync(path.join(repository, "catalog.json"), "utf8"));
  assert.ok(Array.isArray(catalog.plugins), "catalog.plugins 必须是数组");

  for (const plugin of catalog.plugins.filter(item => item?.hasReadme === true)) {
    const artifactName = String(plugin.artifactName || "");
    assert.match(artifactName, /^[A-Za-z][A-Za-z0-9]{0,63}$/u, `artifactName 不符合官方目录安全约束：${artifactName}`);
    assert.match(artifactName, /[A-Z]/u, `artifactName 必须包含大写字母：${artifactName}`);
    const relativeReadme = path.join("plugins", sourceCategory(plugin.kind), artifactName, "README.md");
    assert.ok(
      fs.existsSync(path.join(repository, relativeReadme)),
      `${artifactName} 的 catalog hasReadme=true，但源码 README 不存在：${relativeReadme}`,
    );
  }
});
