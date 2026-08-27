import http from "node:http";
import { deflateRawSync } from "node:zlib";
import fs from "node:fs";
import path from "node:path";
import { createHash } from "node:crypto";
import { deriveCandidateVersion, readProjectVersion } from "../../support/project-version.mjs";
import { projectRoot, runtimeDir } from "./helpers.mjs";

/**
 * 更新源 stub（下一候选版本，e2e）：以本地 HTTP 服务模拟 GitHub Releases API 兼容源。
 * ZIP 内容 = 当前 runtime 构建（exe + wwwroot + plugins/.nxp-root），布局与发布资产一致（flat root）。
 */
export const UPDATE_PORT = 58931;
export const UPDATE_VERSION = deriveCandidateVersion(readProjectVersion(projectRoot));
export const UPDATE_BASE = `http://127.0.0.1:${UPDATE_PORT}/`;
const pluginRepoRoot = path.resolve(projectRoot, "..", "NexusPipeline-Plugins");

let server = null;
let zipBuffer = null;
let shaText = "";

function zipName() {
  return `NexusPipeline-v${UPDATE_VERSION}-win-x64.zip`;
}

function crc32(buffer) {
  let crc = 0xFFFFFFFF;
  for (const byte of buffer) {
    crc ^= byte;
    for (let bit = 0; bit < 8; bit++) {
      crc = (crc >>> 1) ^ (crc & 1 ? 0xEDB88320 : 0);
    }
  }
  return (crc ^ 0xFFFFFFFF) >>> 0;
}

function collectFiles(root, prefix = "") {
  const files = [];
  if (!fs.existsSync(root)) return files;
  for (const entry of fs.readdirSync(root, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name))) {
    const absolute = path.join(root, entry.name);
    const name = prefix ? `${prefix}/${entry.name}` : entry.name;
    if (entry.isDirectory()) files.push(...collectFiles(absolute, name));
    else if (entry.isFile()) files.push({ name, absolute });
  }
  return files;
}

function createZip(files) {
  const localParts = [];
  const centralParts = [];
  let offset = 0;
  const now = new Date();
  const dosTime = (now.getHours() << 11) | (now.getMinutes() << 5) | Math.floor(now.getSeconds() / 2);
  const dosDate = ((now.getFullYear() - 1980) << 9) | ((now.getMonth() + 1) << 5) | now.getDate();

  for (const file of files) {
    const name = Buffer.from(file.name, "utf8");
    const source = fs.readFileSync(file.absolute);
    const compressed = deflateRawSync(source);
    const method = compressed.length < source.length ? 8 : 0;
    const payload = method === 8 ? compressed : source;
    const checksum = crc32(source);

    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034B50, 0);
    local.writeUInt16LE(20, 4);
    local.writeUInt16LE(0x800, 6);
    local.writeUInt16LE(method, 8);
    local.writeUInt16LE(dosTime, 10);
    local.writeUInt16LE(dosDate, 12);
    local.writeUInt32LE(checksum, 14);
    local.writeUInt32LE(payload.length, 18);
    local.writeUInt32LE(source.length, 22);
    local.writeUInt16LE(name.length, 26);
    local.writeUInt16LE(0, 28);
    localParts.push(local, name, payload);

    const central = Buffer.alloc(46);
    central.writeUInt32LE(0x02014B50, 0);
    central.writeUInt16LE(20, 4);
    central.writeUInt16LE(20, 6);
    central.writeUInt16LE(0x800, 8);
    central.writeUInt16LE(method, 10);
    central.writeUInt16LE(dosTime, 12);
    central.writeUInt16LE(dosDate, 14);
    central.writeUInt32LE(checksum, 16);
    central.writeUInt32LE(payload.length, 20);
    central.writeUInt32LE(source.length, 24);
    central.writeUInt16LE(name.length, 28);
    central.writeUInt16LE(0, 30);
    central.writeUInt16LE(0, 32);
    central.writeUInt16LE(0, 34);
    central.writeUInt16LE(0, 36);
    central.writeUInt32LE(0, 38);
    central.writeUInt32LE(offset, 42);
    centralParts.push(central, name);

    offset += local.length + name.length + payload.length;
  }

  const body = Buffer.concat(localParts);
  const centralDirectory = Buffer.concat(centralParts);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054B50, 0);
  end.writeUInt16LE(0, 4);
  end.writeUInt16LE(0, 6);
  end.writeUInt16LE(files.length, 8);
  end.writeUInt16LE(files.length, 10);
  end.writeUInt32LE(centralDirectory.length, 12);
  end.writeUInt32LE(body.length, 16);
  end.writeUInt16LE(0, 20);
  return Buffer.concat([body, centralDirectory, end]);
}

function buildZip() {
  const files = [
    { name: "nexus-pipeline.exe", absolute: path.join(runtimeDir, "nexus-pipeline.exe") },
    ...collectFiles(path.join(runtimeDir, "wwwroot"), "wwwroot"),
    ...collectFiles(path.join(runtimeDir, "plugins"), "plugins"),
  ];
  zipBuffer = createZip(files);
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

function pluginCatalogJson() {
  const file = path.join(pluginRepoRoot, "catalog.json");
  return fs.existsSync(file) ? fs.readFileSync(file, "utf8") : JSON.stringify({ schemaVersion: 1, repository: "FlappiBakuse/NexusPipeline-Plugins", generatedAt: new Date().toISOString(), plugins: [] });
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
    if (url.pathname === "/plugins/catalog.json") {
      const body = pluginCatalogJson();
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
