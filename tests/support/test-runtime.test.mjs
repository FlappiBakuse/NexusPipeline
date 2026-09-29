import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { stopSpawnedService } from "./test-runtime.mjs";


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
