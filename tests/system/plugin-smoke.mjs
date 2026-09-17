import test, { after, before } from "node:test";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import http from "node:http";
import fs from "node:fs";
import path from "node:path";
import {
  api,
  executionMode,
  isAdminMode,
  isAdministrator,
  prepareRuntime,
  projectRoot,
  runtimeDir,
  startRuntime,
  stopRuntime,
  waitForService,
} from "./runtime-helper.mjs";

const enabled = process.env.NEXUS_SYSTEM_SMOKE === "1";
const skip = enabled ? false : "设置 NEXUS_SYSTEM_SMOKE=1 后运行";

const pluginName = "bettergi";
const artifactName = "BetterGI";
const pluginVersions = ["0.2.9", "0.2.10"];
const pluginRepositoryRoot = process.env.NEXUS_PLUGIN_REPO_ROOT?.trim()
  ? (path.isAbsolute(process.env.NEXUS_PLUGIN_REPO_ROOT.trim())
    ? process.env.NEXUS_PLUGIN_REPO_ROOT.trim()
    : path.resolve(projectRoot, process.env.NEXUS_PLUGIN_REPO_ROOT.trim()))
  : path.resolve(projectRoot, "..", "NexusPipeline-Plugins");
const packageRoot = path.join(pluginRepositoryRoot, "packages", artifactName);
const pluginStateDir = path.join(runtimeDir, ".nxp", "state", "plugins");
const pendingPath = path.join(pluginStateDir, "pending.json");
const ownershipPath = path.join(pluginStateDir, "ownership.json");
let pluginServer = null;
let pluginServerPort = null;
let catalogVersion = pluginVersions[0];
let baseCatalog = null;
let originalCatalogUrl;
let originalPackageBaseUrl;
const pluginServerRequests = [];

function packagePath(version) {
  return path.join(packageRoot, `${artifactName}-${version}.zip`);
}

function catalogEntry(version) {
  const entry = structuredClone(baseCatalog.plugins.find(item => item.name === pluginName));
  const packageFile = packagePath(version);
  const bytes = fs.readFileSync(packageFile);
  entry.version = version;
  entry.packageUrl = `http://127.0.0.1:${pluginServerPort}/packages/${artifactName}/${artifactName}-${version}.zip`;
  entry.sizeBytes = bytes.length;
  entry.sha256 = createHash("sha256").update(bytes).digest("hex");
  entry.minHostVersion = version === "0.2.9" ? "0.14.4" : "0.15.11";
  entry.updatedAt = "2026-09-17";
  entry.changelog = [{ version, date: "2026-09-17", items: [`System Smoke fixture ${version}`] }];
  for (const locale of Object.values(entry.locales || {})) {
    locale.changelog = [{ version, items: [`System Smoke fixture ${version}`] }];
  }
  return entry;
}

function catalogPayload() {
  return JSON.stringify({
    schemaVersion: 2,
    repository: "FlappiBakuse/NexusPipeline-Plugins",
    generatedAt: new Date().toISOString(),
    plugins: [catalogEntry(catalogVersion)],
  });
}

async function startPluginRepositoryStub() {
  const catalogPath = path.join(pluginRepositoryRoot, "catalog.json");
  assert.equal(fs.existsSync(catalogPath), true, `缺少插件 catalog：${catalogPath}`);
  baseCatalog = JSON.parse(fs.readFileSync(catalogPath, "utf8"));
  for (const version of pluginVersions) {
    assert.equal(fs.existsSync(packagePath(version)), true, `缺少插件 A/B 包：${packagePath(version)}`);
  }

  pluginServer = http.createServer((request, response) => {
    const url = new URL(request.url || "/", "http://127.0.0.1/");
    pluginServerRequests.push(`${request.method} ${url.pathname}`);
    if (request.method === "GET" && url.pathname === "/catalog.json") {
      const body = catalogPayload();
      response.writeHead(200, {
        "Content-Type": "application/json",
        "Content-Length": Buffer.byteLength(body),
      });
      response.end(body);
      return;
    }
    const packagePrefix = `/packages/${artifactName}/`;
    if (request.method === "GET" && url.pathname.startsWith(packagePrefix)) {
      const version = url.pathname.slice(packagePrefix.length)
        .replace(`${artifactName}-`, "")
        .replace(/\.zip$/u, "");
      if (pluginVersions.includes(version)) {
        const body = fs.readFileSync(packagePath(version));
        response.writeHead(200, {
          "Content-Type": "application/zip",
          "Content-Length": body.length,
        });
        response.end(body);
        return;
      }
    }
    response.writeHead(404);
    response.end("not found");
  });
  await new Promise((resolve, reject) => {
    pluginServer.once("error", reject);
    pluginServer.listen(0, "127.0.0.1", resolve);
  });
  pluginServerPort = pluginServer.address().port;
  originalCatalogUrl = process.env.NEXUS_PLUGIN_CATALOG_URL;
  originalPackageBaseUrl = process.env.NEXUS_PLUGIN_PACKAGE_BASE_URL;
  process.env.NEXUS_PLUGIN_CATALOG_URL = `http://127.0.0.1:${pluginServerPort}/catalog.json`;
  process.env.NEXUS_PLUGIN_PACKAGE_BASE_URL = `http://127.0.0.1:${pluginServerPort}/packages/`;
}

async function stopPluginRepositoryStub() {
  if (pluginServer) {
    const closing = pluginServer;
    pluginServer = null;
    await new Promise(resolve => closing.close(resolve));
  }
  if (originalCatalogUrl === undefined) delete process.env.NEXUS_PLUGIN_CATALOG_URL;
  else process.env.NEXUS_PLUGIN_CATALOG_URL = originalCatalogUrl;
  if (originalPackageBaseUrl === undefined) delete process.env.NEXUS_PLUGIN_PACKAGE_BASE_URL;
  else process.env.NEXUS_PLUGIN_PACKAGE_BASE_URL = originalPackageBaseUrl;
}

function readJson(file, fallback) {
  return fs.existsSync(file)
    ? JSON.parse(fs.readFileSync(file, "utf8").replace(/^\uFEFF/u, ""))
    : fallback;
}

async function restartRuntime() {
  await stopRuntime();
  startRuntime(["web"]);
  await waitForService(null, 60000);
}

function findPlugin(plugins) {
  return plugins.find(item => item.name === pluginName) || null;
}

before(async () => {
  if (!enabled) return;
  if (isAdminMode) assert.ok(isAdministrator(), "管理员 plugin Smoke 必须在 Administrator / High Integrity 终端运行");
  await startPluginRepositoryStub();
  prepareRuntime();
  fs.mkdirSync(path.join(runtimeDir, "plugins"), { recursive: true });
  fs.writeFileSync(path.join(runtimeDir, "plugins", "acceptance-witness.txt"), "keep-through-plugin-lifecycle", "utf8");
  startRuntime();
  await waitForService();
});

after(async () => {
  if (enabled) await stopRuntime();
  await stopPluginRepositoryStub();
});

test(`${executionMode} 插件管理与前端运行时清单使用稳定 JSON 形状`, { skip }, async () => {
  const pluginsResponse = await api("GET", "/api/plugins");
  const plugins = await pluginsResponse.json();
  assert.equal(pluginsResponse.status, 200);
  assert.ok(Array.isArray(plugins));
  for (const plugin of plugins) {
    assert.equal(typeof plugin.name, "string");
    assert.equal(typeof plugin.kind, "string");
    assert.equal(typeof plugin.compatible, "boolean");
  }

  const runtimeResponse = await api("GET", "/api/plugin-runtime/frontend");
  const runtime = await runtimeResponse.json();
  assert.equal(runtimeResponse.status, 200);
  assert.ok(Array.isArray(runtime));
  for (const descriptor of runtime) {
    assert.equal(typeof descriptor.name, "string");
    assert.equal(typeof descriptor.entryUrl, "string");
    assert.equal(typeof descriptor.frontendApiVersion, "string");
  }
});

test("插件诊断同时覆盖 runtime 与 pending 安装状态", { skip }, async () => {
  const response = await api("GET", "/api/diagnostics");
  const body = await response.json();
  assert.equal(response.status, 200);
  for (const id of ["plugin.runtime", "plugin.pending"]) {
    const check = body.checks?.find(item => item.id === id);
    assert.ok(check, `缺少诊断项 ${id}`);
    assert.match(String(check.status), /^(pass|warn|fail|skipped)$/);
  }
});

test(`${executionMode} 官方插件包经真实 API、pending journal 与连续重启完成安装→更新→卸载`, { skip, concurrency: false }, async () => {
  const witness = path.join(runtimeDir, "plugins", "acceptance-witness.txt");
  assert.equal(fs.readFileSync(witness, "utf8"), "keep-through-plugin-lifecycle");

  const installResponse = await api("POST", `/api/plugins/store/${pluginName}/install`);
  const installBody = await installResponse.json();
  assert.equal(installResponse.status, 200, `${JSON.stringify(installBody)} requests=${pluginServerRequests.join(",")}`);
  assert.equal(installBody.pending, true);
  assert.equal(installBody.action, "install");
  assert.equal(installBody.version, "0.2.9");
  const pendingInstall = readJson(pendingPath, null);
  assert.equal(pendingInstall?.Operations?.length, 1);
  assert.equal(pendingInstall.Operations[0].Name, pluginName);
  assert.equal(pendingInstall.Operations[0].Phase, "pending");

  await restartRuntime();
  let pluginsResponse = await api("GET", "/api/plugins");
  let plugins = await pluginsResponse.json();
  let plugin = findPlugin(plugins);
  assert.equal(pluginsResponse.status, 200);
  assert.ok(plugin);
  assert.equal(plugin.version, "0.2.9");
  assert.equal(plugin.artifactName, artifactName);
  assert.equal(plugin.kind, "data-specialized");
  assert.equal(plugin.managedByStore, true);
  assert.equal(plugin.installedVersion, "0.2.9");
  assert.equal(plugin.pendingAction, "");
  assert.equal(readJson(pendingPath, { Operations: [] }).Operations.length, 0);
  assert.equal(readJson(ownershipPath, { Plugins: [] }).Plugins[0]?.Version, "0.2.9");
  assert.equal(fs.readFileSync(witness, "utf8"), "keep-through-plugin-lifecycle");

  catalogVersion = "0.2.10";
  const refreshResponse = await api("POST", "/api/plugins/store/refresh");
  const refreshBody = await refreshResponse.json();
  assert.equal(refreshResponse.status, 200, JSON.stringify(refreshBody));
  assert.equal(refreshBody.plugins.find(item => item.name === pluginName)?.version, "0.2.10");
  const updateResponse = await api("POST", `/api/plugins/store/${pluginName}/update`);
  const updateBody = await updateResponse.json();
  assert.equal(updateResponse.status, 200, JSON.stringify(updateBody));
  assert.equal(updateBody.pending, true);
  assert.equal(updateBody.action, "update");
  assert.equal(updateBody.version, "0.2.10");
  assert.equal(readJson(pendingPath, null)?.Operations[0]?.Phase, "pending");

  await restartRuntime();
  pluginsResponse = await api("GET", "/api/plugins");
  plugins = await pluginsResponse.json();
  plugin = findPlugin(plugins);
  assert.ok(plugin);
  assert.equal(plugin.version, "0.2.10");
  assert.equal(plugin.installedVersion, "0.2.10");
  assert.equal(plugin.managedByStore, true);
  assert.equal(plugin.pendingAction, "");
  assert.equal(readJson(ownershipPath, { Plugins: [] }).Plugins[0]?.Version, "0.2.10");
  assert.equal(fs.readFileSync(witness, "utf8"), "keep-through-plugin-lifecycle");

  const uninstallResponse = await api("POST", `/api/plugins/store/${pluginName}/uninstall`);
  const uninstallBody = await uninstallResponse.json();
  assert.equal(uninstallResponse.status, 200, JSON.stringify(uninstallBody));
  assert.equal(uninstallBody.pending, true);
  assert.equal(uninstallBody.action, "uninstall");
  assert.equal(uninstallBody.version, "0.2.10");
  assert.equal(readJson(pendingPath, null)?.Operations[0]?.Phase, "pending");

  await restartRuntime();
  pluginsResponse = await api("GET", "/api/plugins");
  plugins = await pluginsResponse.json();
  assert.equal(pluginsResponse.status, 200);
  assert.equal(findPlugin(plugins), null);
  assert.equal(readJson(pendingPath, { Operations: [] }).Operations.length, 0);
  assert.equal(readJson(ownershipPath, { Plugins: [] }).Plugins.length, 0);
  assert.equal(fs.existsSync(path.join(runtimeDir, "plugins", artifactName)), false);
  assert.equal(fs.readFileSync(witness, "utf8"), "keep-through-plugin-lifecycle");

  await restartRuntime();
  pluginsResponse = await api("GET", "/api/plugins");
  plugins = await pluginsResponse.json();
  assert.equal(pluginsResponse.status, 200);
  assert.equal(findPlugin(plugins), null);
  assert.equal(readJson(pendingPath, { Operations: [] }).Operations.length, 0);
  assert.equal(readJson(ownershipPath, { Plugins: [] }).Plugins.length, 0);
  assert.equal(fs.readFileSync(witness, "utf8"), "keep-through-plugin-lifecycle");
});
