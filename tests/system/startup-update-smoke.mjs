import test, { afterEach, beforeEach } from "node:test";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { spawnSync } from "node:child_process";
import http from "node:http";
import fs from "node:fs";
import path from "node:path";
import {
  api,
  prepareRuntime,
  projectRoot,
  runtimeDir,
  runtimeDiagnostic,
  sleep,
  startRuntime,
  systemWebPort,
  stopRuntime,
  waitFor,
  waitForService,
} from "./runtime-helper.mjs";
import { deriveCandidateVersion, readProjectVersion } from "../support/project-version.mjs";

const enabled = process.env.NEXUS_SYSTEM_SMOKE === "1";
const skipReason = enabled ? false : "设置 NEXUS_SYSTEM_SMOKE=1 后运行";
const updateVersion = deriveCandidateVersion(readProjectVersion(projectRoot));

function writeSettings(updateCheckEnabled, updateAutoApplyEnabled = false) {
  fs.mkdirSync(path.join(runtimeDir, "config"), { recursive: true });
  fs.writeFileSync(
    path.join(runtimeDir, "config", "settings.json"),
    JSON.stringify({
      WebPort: systemWebPort,
      UpdateCheckEnabled: updateCheckEnabled,
      UpdateAutoApplyEnabled: updateAutoApplyEnabled,
      PluginAutoUpdateEnabled: false,
      AutoOpenBrowser: false,
    }),
    "utf8",
  );
}

function createReleaseServer(packageFixture = null) {
  let releaseRequests = 0;
  let packageRequests = 0;
  let checksumRequests = 0;
  const server = http.createServer((request, response) => {
    if (request.url === "/update-policy.json") {
      response.writeHead(200, { "Content-Type": "application/json" });
      response.end(JSON.stringify({
        schemaVersion: 1,
        repository: "FlappiBakuse/NexusPipeline",
        barriers: [],
      }));
      return;
    }
    if (request.url === "/releases") {
      releaseRequests++;
      const release = {
        draft: false,
        prerelease: true,
        tag_name: `v${updateVersion}`,
        name: `v${updateVersion} test release`,
        body: "periodic update regression fixture",
        assets: [
          {
            name: `NexusPipeline-v${updateVersion}-win-x64.zip`,
            browser_download_url: `http://127.0.0.1:${server.address()?.port || 0}/NexusPipeline-v${updateVersion}-win-x64.zip`,
          },
          {
            name: `NexusPipeline-v${updateVersion}-win-x64.zip.sha256`,
            browser_download_url: `http://127.0.0.1:${server.address()?.port || 0}/NexusPipeline-v${updateVersion}-win-x64.zip.sha256`,
          },
        ],
      };
      response.writeHead(200, { "Content-Type": "application/json" });
      response.end(JSON.stringify([release]));
      return;
    }
    if (request.url?.endsWith(".zip.sha256") && packageFixture) {
      checksumRequests++;
      response.writeHead(200, { "Content-Type": "text/plain" });
      response.end(`${packageFixture.sha256}\n`);
      return;
    }
    if (request.url?.endsWith(".zip") && packageFixture) {
      packageRequests++;
      response.writeHead(200, { "Content-Type": "application/zip", "Content-Length": packageFixture.bytes.length });
      response.end(packageFixture.bytes);
      return;
    }
    response.writeHead(404);
    response.end();
  });
  return {
    server,
    get releaseRequests() {
      return releaseRequests;
    },
    get packageRequests() {
      return packageRequests;
    },
    get checksumRequests() {
      return checksumRequests;
    },
  };
}

function createUpdatePackageFixture() {
  const packageRoot = path.join(runtimeDir, ".startup-update-package-src");
  const zipPath = path.join(runtimeDir, ".startup-update-package.zip");
  fs.rmSync(packageRoot, { recursive: true, force: true });
  fs.rmSync(zipPath, { force: true });
  fs.mkdirSync(packageRoot, { recursive: true });
  fs.copyFileSync(path.join(runtimeDir, "nexus-pipeline.exe"), path.join(packageRoot, "nexus-pipeline.exe"));
  fs.cpSync(path.join(runtimeDir, "wwwroot"), path.join(packageRoot, "wwwroot"), { recursive: true });
  fs.writeFileSync(path.join(packageRoot, "wwwroot", "startup-update-marker.txt"), "startup-update-installed", "utf8");

  const packageScript = [
    "from pathlib import Path",
    "import sys, zipfile",
    "root, destination = Path(sys.argv[1]), Path(sys.argv[2])",
    "with zipfile.ZipFile(destination, 'w', compression=zipfile.ZIP_DEFLATED) as archive:",
    "    for entry in (root / 'nexus-pipeline.exe', root / 'wwwroot'):",
    "        if entry.is_dir():",
    "            for file in entry.rglob('*'):",
    "                if file.is_file(): archive.write(file, file.relative_to(root).as_posix())",
    "        elif entry.is_file(): archive.write(entry, entry.relative_to(root).as_posix())",
  ].join("\n");
  const archived = spawnSync("python", ["-c", packageScript, packageRoot, zipPath], {
    encoding: "utf8",
    windowsHide: true,
  });
  if (archived.error || archived.status !== 0 || !fs.existsSync(zipPath)) {
    throw new Error(`无法构造启动更新包：${archived.error?.message || archived.stderr || archived.stdout || archived.status}`);
  }
  const bytes = fs.readFileSync(zipPath);
  fs.rmSync(packageRoot, { recursive: true, force: true });
  fs.rmSync(zipPath, { force: true });
  return { bytes, sha256: createHash("sha256").update(bytes).digest("hex") };
}

async function listen(server) {
  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", resolve);
  });
  return `http://127.0.0.1:${server.address().port}/releases`;
}

async function close(server) {
  if (!server.listening) return;
  await new Promise((resolve, reject) => {
    server.close(error => error ? reject(error) : resolve());
  });
}

beforeEach(async () => {
  if (!enabled) return;
  prepareRuntime();
});

afterEach(async () => {
  if (enabled) await stopRuntime();
});

test("定期自动检查：开启时服务启动后完成首次检查", { skip: skipReason, concurrency: false }, async () => {
  const fixture = createReleaseServer();
  const sourceUrl = await listen(fixture.server);
  writeSettings(true);
  try {
    startRuntime(["web"], { NEXUS_UPDATE_URL: sourceUrl });
    await waitForService();
    const observed = await waitFor(async () => {
      if (fixture.releaseRequests <= 0) return false;
      const response = await api("GET", "/api/update/status");
      if (!response.ok) return false;
      const status = await response.json();
      return status.available === true && status.latest === updateVersion;
    }, 15000, 100);
    let diagnosticStatus = "";
    try {
      const response = await api("GET", "/api/update/status");
      diagnosticStatus = `${response.status} ${await response.text()}`;
    } catch (error) {
      diagnosticStatus = error?.stack || error?.message || String(error);
    }
    assert.equal(observed, true, `宿主启动后应自动请求更新源；requests=${fixture.requestCount}; status=${diagnosticStatus}\n${runtimeDiagnostic()}`);
    assert.equal(fixture.releaseRequests, 1, "首次检查观察窗口内不得重复触发");
    const status = await (await api("GET", "/api/update/status")).json();
    assert.equal(status.available, true);
    assert.equal(status.latest, updateVersion);
    assert.equal(status.automation.checkEnabled, true);
    assert.ok(status.automation.lastCheckAt);
    assert.ok(status.automation.nextCheckAt);
  } finally {
    await close(fixture.server);
  }
});

test("服务模式定期自动检查：开启时完成首次检查", { skip: skipReason, concurrency: false }, async () => {
  const fixture = createReleaseServer();
  const sourceUrl = await listen(fixture.server);
  writeSettings(true);
  try {
    startRuntime(["service"], { NEXUS_UPDATE_URL: sourceUrl });
    await waitForService();
    const observed = await waitFor(() => fixture.releaseRequests > 0, 15000, 100);
    assert.equal(observed, true, "服务宿主启动后应自动请求更新源");
    assert.equal(fixture.releaseRequests, 1, "服务模式首次检查观察窗口内不得重复触发");
  } finally {
    await close(fixture.server);
  }
});

test("定期自动检查：关闭时不请求更新源", { skip: skipReason, concurrency: false }, async () => {
  const fixture = createReleaseServer();
  const sourceUrl = await listen(fixture.server);
  writeSettings(false);
  try {
    startRuntime(["web"], { NEXUS_UPDATE_URL: sourceUrl });
    await waitForService();
    await sleep(2000);
    assert.equal(fixture.releaseRequests, 0, "关闭自动检查时不得请求更新源");
    const status = await (await api("GET", "/api/update/status")).json();
    assert.equal(status.automation.checkEnabled, false);
    assert.equal(status.automation.autoUpdateEnabled, false);
  } finally {
    await close(fixture.server);
  }
});

test("闲时自动更新开关：启动时下载并应用后再启动 Web 服务", { skip: skipReason, concurrency: false }, async () => {
  const packageFixture = createUpdatePackageFixture();
  const fixture = createReleaseServer(packageFixture);
  const sourceUrl = await listen(fixture.server);
  writeSettings(true, true);
  try {
    startRuntime(["web"], { NEXUS_UPDATE_URL: sourceUrl });
    await waitForService(null, 90000);

    const installed = path.join(runtimeDir, "wwwroot", "startup-update-marker.txt");
    assert.equal(
      fs.existsSync(installed),
      true,
      `首次成功响应时更新包应已应用，随后才启动 Web 服务\n${runtimeDiagnostic()}`,
    );
    assert.equal(fs.readFileSync(installed, "utf8"), "startup-update-installed");
    assert.equal(fixture.packageRequests, 1, "启动更新只能下载一次；恢复后的同目标启动应受冷却标记保护");
    assert.equal(fixture.checksumRequests, 1);
    assert.ok(fixture.releaseRequests >= 2, "更新后的宿主应执行下一次启动检查");
    const status = await (await api("GET", "/api/status")).json();
    assert.ok(status.version);
  } finally {
    await close(fixture.server);
  }
});
