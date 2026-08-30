import http from "node:http";
import fs from "node:fs";
import path from "node:path";
import { pluginRepositoryRoot } from "./helpers.mjs";

/**
 * UI Smoke 本地 stub 源：更新检查返回空 releases，阻断服务启动检查对真实 GitHub 的网络依赖；
 * 插件仓库 catalog 来自相邻 NexusPipeline-Plugins 仓库。更新事务验证由 tests/system/update-smoke.mjs 承担。
 */
export const UPDATE_PORT = 58931;
export const UPDATE_BASE = `http://127.0.0.1:${UPDATE_PORT}/`;

let server = null;

function pluginCatalogJson() {
  const file = path.join(pluginRepositoryRoot(), "catalog.json");
  return fs.existsSync(file)
    ? fs.readFileSync(file, "utf8")
    : JSON.stringify({ schemaVersion: 1, repository: "FlappiBakuse/NexusPipeline-Plugins", generatedAt: new Date().toISOString(), plugins: [] });
}

export async function startUpdateStub() {
  if (server) return;
  server = http.createServer((req, res) => {
    const url = new URL(req.url, UPDATE_BASE);
    if (url.pathname === "/plugins/catalog.json") {
      const body = pluginCatalogJson();
      res.writeHead(200, { "Content-Type": "application/json", "Content-Length": Buffer.byteLength(body) });
      res.end(body);
      return;
    }
    if (url.pathname === "/" || url.pathname === "/releases") {
      const body = "[]";
      res.writeHead(200, { "Content-Type": "application/json", "Content-Length": Buffer.byteLength(body) });
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
