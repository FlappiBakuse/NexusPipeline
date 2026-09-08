import test, { afterEach, beforeEach } from "node:test";
import assert from "node:assert/strict";
import http from "node:http";
import fs from "node:fs";
import path from "node:path";
import {
  api,
  prepareRuntime,
  projectRoot,
  runtimeDir,
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

function writeSettings(updateCheckEnabled) {
  fs.mkdirSync(path.join(runtimeDir, "config"), { recursive: true });
  fs.writeFileSync(
    path.join(runtimeDir, "config", "settings.json"),
    JSON.stringify({
      WebPort: systemWebPort,
      UpdateCheckEnabled: updateCheckEnabled,
      UpdateAutoApplyEnabled: false,
      AutoOpenBrowser: false,
    }),
    "utf8",
  );
}

function createReleaseServer() {
  let requestCount = 0;
  const server = http.createServer((request, response) => {
    if (request.url === "/releases") {
      requestCount++;
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
    response.writeHead(404);
    response.end();
  });
  return {
    server,
    get requestCount() {
      return requestCount;
    },
  };
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
      if (fixture.requestCount <= 0) return false;
      const response = await api("GET", "/api/update/status");
      if (!response.ok) return false;
      const status = await response.json();
      return status.available === true && status.latest === updateVersion;
    }, 15000, 100);
    assert.equal(observed, true, "宿主启动后应自动请求更新源");
    assert.equal(fixture.requestCount, 1, "首次检查观察窗口内不得重复触发");
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
    const observed = await waitFor(() => fixture.requestCount > 0, 15000, 100);
    assert.equal(observed, true, "服务宿主启动后应自动请求更新源");
    assert.equal(fixture.requestCount, 1, "服务模式首次检查观察窗口内不得重复触发");
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
    assert.equal(fixture.requestCount, 0, "关闭自动检查时不得请求更新源");
    const status = await (await api("GET", "/api/update/status")).json();
    assert.equal(status.automation.checkEnabled, false);
    assert.equal(status.automation.autoUpdateEnabled, false);
  } finally {
    await close(fixture.server);
  }
});
