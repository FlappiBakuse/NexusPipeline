import http from "node:http";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { createHash } from "node:crypto";
import { spawnSync } from "node:child_process";
import { runtimeDir } from "./helpers.mjs";

/**
 * 更新源 stub（v0.10.1，e2e）：以本地 http 服务模拟 GitHub Releases API 兼容源。
 * zip 内容 = 当前 release 构建（exe + wwwroot + plugins），布局与发布资产一致（flat root）。
 * global setup 启动、global teardown 关闭；服务经 NEXUS_UPDATE_URL 指向本 stub。
 */
export const UPDATE_PORT = 58931;
export const UPDATE_VERSION = "0.10.2";
export const UPDATE_BASE = `http://127.0.0.1:${UPDATE_PORT}/`;

let server = null;
let zipBuffer = null;
let shaText = "";

function zipName() {
  return `NexusPipeline-v${UPDATE_VERSION}-win-x64.zip`;
}

function buildZip() {
  const stamp = `${Date.now()}-${Math.random().toString(36).slice(2)}`;
  const bundle = path.join(os.tmpdir(), `nxp-update-bundle-${stamp}`);
  fs.mkdirSync(path.join(bundle, "wwwroot"), { recursive: true });
  fs.copyFileSync(path.join(runtimeDir, "nexus-pipeline.exe"), path.join(bundle, "nexus-pipeline.exe"));
  fs.cpSync(path.join(runtimeDir, "wwwroot"), path.join(bundle, "wwwroot"), { recursive: true });
  const plugins = path.join(runtimeDir, "plugins");
  if (fs.existsSync(plugins)) {
    fs.cpSync(plugins, path.join(bundle, "plugins"), { recursive: true });
  }
  const zipPath = path.join(os.tmpdir(), `${zipName()}-${stamp}.zip`);
  const args = ["-NoProfile", "-NonInteractive", "-Command",
    `Compress-Archive -Path '${bundle}\\nexus-pipeline.exe','${bundle}\\wwwroot','${bundle}\\plugins' -DestinationPath '${zipPath}' -CompressionLevel Fastest`];
  const result = spawnSync("pwsh", args, { encoding: "utf8", windowsHide: true });
  fs.rmSync(bundle, { recursive: true, force: true });
  if (result.status !== 0 || !fs.existsSync(zipPath)) {
    throw new Error(`构建更新 stub zip 失败：${result.stderr || "未知错误"}`);
  }
  zipBuffer = fs.readFileSync(zipPath);
  fs.rmSync(zipPath, { force: true });
  shaText = createHash("sha256").update(zipBuffer).digest("hex");
}

function releasesJson() {
  const zipUrl = UPDATE_BASE + zipName();
  return JSON.stringify([
    {
      tag_name: `v${UPDATE_VERSION}`,
      name: `v${UPDATE_VERSION}`,
      draft: false,
      prerelease: true,
      body: "UI Smoke 更新说明",
      assets: [
        { name: zipName(), browser_download_url: zipUrl },
        { name: zipName() + ".sha256", browser_download_url: zipUrl + ".sha256" },
      ],
    },
  ]);
}

export async function startUpdateStub() {
  if (server) return;
  buildZip();
  server = http.createServer((req, res) => {
    const url = new URL(req.url, UPDATE_BASE);
    if (url.pathname === "/" || url.pathname === "/releases") {
      const body = releasesJson();
      res.writeHead(200, { "Content-Type": "application/json", "Content-Length": Buffer.byteLength(body) });
      res.end(body);
      return;
    }
    if (url.pathname.endsWith(".zip")) {
      res.writeHead(200, { "Content-Type": "application/octet-stream", "Content-Length": zipBuffer.length });
      res.end(zipBuffer);
      return;
    }
    if (url.pathname.endsWith(".sha256")) {
      const body = shaText + "\n";
      res.writeHead(200, { "Content-Type": "text/plain", "Content-Length": Buffer.byteLength(body) });
      res.end(body);
      return;
    }
    res.writeHead(404);
    res.end("not found");
  });
  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(UPDATE_PORT, "127.0.0.1", resolve);
  });
}

export async function stopUpdateStub() {
  if (!server) return;
  const closing = server;
  server = null;
  await new Promise(resolve => closing.close(resolve));
}
