import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { stopSpawnedService } from "./test-runtime.mjs";

test("post-run cleanup requires confirmed exit when OS identity is unavailable", async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "nxp-exit-settlement-"));
  const markerPath = path.join(root, "marker.json"), pidFilePath = path.join(root, "service.pid");
  fs.writeFileSync(markerPath, JSON.stringify({ schemaVersion: 1, nonce: "test", pid: 424242,
    executablePath: process.execPath, processStartTimeUtc: "original" }));
  let alive = true, terminated = false;
  const options = { child: null, markerPath, pidFilePath, exitFile: path.join(root, "exit"),
    allowReusedPid: true, aliveReader: () => alive, identityReader: () => null,
    terminator: () => { terminated = true; return true; } };
  try {
    await stopSpawnedService({ ...options, exitWaiter: async () => { alive = false; return true; } });
    assert.equal(terminated, false);
    alive = true;
    await assert.rejects(stopSpawnedService({ ...options, exitWaiter: async () => false }), /UNKNOWN/);
    assert.equal(terminated, false);
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});


test("post-run cleanup ignores a PID now owned by another process", async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "nxp-reused-pid-"));
  const markerPath = path.join(root, "marker.json");
  const pidFilePath = path.join(root, "service.pid");
  const exitFile = path.join(root, "test-host.exit");
  const pid = 424242;
  fs.writeFileSync(markerPath, JSON.stringify({
    schemaVersion: 1, nonce: "test-nonce", pid,
    executablePath: process.execPath, processStartTimeUtc: "old-process",
    handoffProcesses: [],
  }));
  fs.writeFileSync(pidFilePath, String(pid));
  let terminated = false;
  try {
    await stopSpawnedService({
      child: null, exitFile, pidFilePath, markerPath, allowReusedPid: true,
      aliveReader: () => true,
      identityReader: () => ({ executablePath: process.execPath, startTime: "new-process" }),
      terminator: () => { terminated = true; return true; },
      exitWaiter: () => { throw new Error("unrelated process must not be waited on"); },
    });
    assert.equal(terminated, false);
    assert.equal(fs.readFileSync(pidFilePath, "utf8"), String(pid));
    await assert.rejects(stopSpawnedService({
      child: null, exitFile, pidFilePath, markerPath,
      aliveReader: () => true,
      identityReader: () => ({ executablePath: process.execPath, startTime: "new-process" }),
      terminator: () => { terminated = true; return true; },
    }), /NOT_OWNED/);
    assert.equal(terminated, false);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});
