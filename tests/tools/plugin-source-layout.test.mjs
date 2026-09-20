import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";

function findPluginRepository() {
  const configured = process.env.NEXUS_OFFICIAL_PLUGINS_ROOT?.trim();
  assert.ok(configured, "必须显式设置 NEXUS_OFFICIAL_PLUGINS_ROOT，禁止猜测官方 Plugins 根目录");
  const repository = path.resolve(configured);
  assert.ok(fs.existsSync(path.join(repository, "catalog.json")), `官方插件仓库缺少 catalog.json：${repository}`);
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
