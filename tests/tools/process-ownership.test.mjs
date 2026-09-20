import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import {
  OWNERSHIP,
  ensureOwnedRuntimeDirectory,
  inspectHandoffProcessOwnership,
  inspectProcessOwnership,
  inspectRestartedProcessOwnership,
  registerHandoffProcess,
  stopSpawnedService,
} from "../support/test-runtime.mjs";

test("runtime preparation accepts a confirmed exit race but preserves unknown or reused processes", async () => {
  for (const state of ["exited", "unknown", "reused", "exit-unconfirmed"]) {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), "nxp-prepare-exit-"));
    try {
      const markerPath = path.join(root, "marker.json");
      fs.writeFileSync(markerPath, JSON.stringify({ schemaVersion: 1, nonce: "nonce", pid: 101,
        executablePath: process.execPath, processStartTimeUtc: "original" }));
      const sentinel = path.join(root, "evidence.txt");
      fs.writeFileSync(sentinel, "preserve until exit confirmed");
      let probes = 0;
      let kills = 0;
      let waits = 0;
      const operation = ensureOwnedRuntimeDirectory(root, markerPath, path.join(root, "absent.pid"), {
        aliveReader: () => ++probes === 1 || !["exited", "exit-unconfirmed"].includes(state),
        identityReader: () => state === "reused" ? { executablePath: process.execPath, startTime: "new" } : null,
        terminator: () => { kills++; return true; },
        exitWaiter: async () => { waits++; return state !== "exit-unconfirmed"; },
      });
      if (state === "exited") {
        await operation;
        assert.deepEqual(fs.readdirSync(root), []);
        assert.equal(waits, 1);
      } else {
        await assert.rejects(operation, /拒绝清理运行进程|运行进程退出未确认/);
        assert.equal(fs.readFileSync(sentinel, "utf8"), "preserve until exit confirmed");
      }
      assert.equal(kills, 0);
    } finally { fs.rmSync(root, { recursive: true, force: true }); }
  }
});

test("process ownership treats missing or unverifiable start times as unknown", () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "nxp-ownership-"));
  try {
    const markerPath = path.join(root, "run-marker.json");
    fs.writeFileSync(markerPath, JSON.stringify({
      schemaVersion: 1,
      nonce: "nonce",
      pid: 101,
      executablePath: process.execPath,
      processStartTimeUtc: "",
    }));
    const identity = () => ({ pid: 101, executablePath: process.execPath, startTime: "2026-09-20T00:00:00.000Z", parentPid: null });
    assert.equal(inspectProcessOwnership(markerPath, 101, { aliveReader: () => true, identityReader: identity }), OWNERSHIP.UNKNOWN);
    assert.equal(inspectProcessOwnership(markerPath, 101, { aliveReader: () => false, identityReader: identity }), OWNERSHIP.EXITED);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test("identity query racing graceful exit requires a fresh definitive absence", () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "nxp-exit-race-"));
  try {
    const markerPath = path.join(root, "marker.json");
    fs.writeFileSync(markerPath, JSON.stringify({
      schemaVersion: 1, nonce: "nonce", pid: 101, executablePath: process.execPath,
      processStartTimeUtc: "original",
      handoffProcesses: [{ pid: 202, executablePath: process.execPath, processStartTimeUtc: "handoff" }],
    }));
    for (const [inspect, pid] of [[inspectProcessOwnership, 101], [inspectHandoffProcessOwnership, 202]]) {
      for (const stillAlive of [false, true, "unavailable"]) {
        let probes = 0;
        const result = inspect(markerPath, pid, {
          identityReader: () => null,
          aliveReader: () => {
            if (++probes === 1) return true;
            if (stillAlive === "unavailable") throw new Error("inspection unavailable");
            return stillAlive;
          },
        });
        assert.equal(result, stillAlive === false ? OWNERSHIP.EXITED : OWNERSHIP.UNKNOWN);
      }
    }
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});

test("cleanup never refreshes reused or missing identities and verifies before signalling", async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "nxp-cleanup-ownership-"));
  const previousMode = process.env.NEXUS_TEST_MODE;
  process.env.NEXUS_TEST_MODE = "test-host";
  try {
    const markerPath = path.join(root, "marker.json");
    const pidFilePath = path.join(root, "service.pid");
    const exitFile = path.join(root, "exit");
    fs.writeFileSync(pidFilePath, "101");
    for (const stamp of ["old-time", ""]) {
      const bytes = JSON.stringify({ schemaVersion: 1, nonce: "nonce", pid: 101, executablePath: process.execPath, processStartTimeUtc: stamp });
      fs.writeFileSync(markerPath, bytes);
      let kills = 0;
      await assert.rejects(stopSpawnedService({ markerPath, pidFilePath, exitFile,
        aliveReader: () => true,
        identityReader: () => ({ executablePath: process.execPath, startTime: "new-time" }),
        terminator: () => { kills++; return true; },
      }), /拒绝发送退出信号/);
      assert.equal(kills, 0);
      assert.equal(fs.existsSync(exitFile), false);
      assert.equal(fs.readFileSync(markerPath, "utf8"), bytes);
    }
    fs.unlinkSync(markerPath);
    await assert.rejects(stopSpawnedService({ markerPath, pidFilePath, exitFile, aliveReader: () => true }), /缺少有效运行 marker/);
  } finally {
    if (previousMode === undefined) delete process.env.NEXUS_TEST_MODE; else process.env.NEXUS_TEST_MODE = previousMode;
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test("handoff requires the run receipt, never just a reused parent PID", () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "nxp-handoff-"));
  try {
    const markerPath = path.join(root, "marker.json");
    const identityFile = path.join(root, "receipt.json");
    const marker = { schemaVersion: 1, nonce: "nonce", runId: "run", pid: 101, executablePath: process.execPath, processStartTimeUtc: "old", identityFile };
    fs.writeFileSync(markerPath, JSON.stringify(marker));
    const identityReader = () => ({ executablePath: process.execPath, startTime: "new", parentPid: 101 });
    assert.equal(inspectRestartedProcessOwnership(markerPath, 202, { identityReader, aliveReader: () => true }), OWNERSHIP.UNKNOWN);
    const receipt = { nonce: "wrong", runId: "run", pid: 202, executablePath: process.execPath, processStartTimeUtc: "new", instanceId: "instance", restartHandoffId: "handoff" };
    fs.writeFileSync(identityFile, JSON.stringify(receipt));
    registerHandoffProcess(markerPath, 202, { identityReader });
    assert.equal(inspectHandoffProcessOwnership(markerPath, 202, { identityReader, aliveReader: () => true }), OWNERSHIP.UNKNOWN);
    fs.writeFileSync(identityFile, JSON.stringify({ ...receipt, nonce: "nonce" }));
    registerHandoffProcess(markerPath, 202, { identityReader, expectedInstanceId: "instance", expectedHandoffId: "handoff" });
    assert.equal(inspectHandoffProcessOwnership(markerPath, 202, { identityReader, aliveReader: () => true }), OWNERSHIP.OWNED);
    registerHandoffProcess(markerPath, 202, { identityReader: () => ({ ...identityReader(), startTime: "reused" }) });
    assert.equal(inspectHandoffProcessOwnership(markerPath, 202, { identityReader: () => ({ ...identityReader(), startTime: "reused" }), aliveReader: () => true }), OWNERSHIP.NOT_OWNED);
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});

test("restarted and handoff ownership require the full observed identity", () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "nxp-ownership-"));
  try {
    const markerPath = path.join(root, "run-marker.json");
    fs.writeFileSync(markerPath, JSON.stringify({
      schemaVersion: 1,
      nonce: "nonce",
      pid: 101,
      executablePath: process.execPath,
      processStartTimeUtc: "2026-09-19T00:00:00.000Z",
      handoffProcesses: [{ pid: 202, executablePath: process.execPath, processStartTimeUtc: "2026-09-20T00:00:00.000Z" }],
    }));
    const identity = () => ({ pid: 202, executablePath: process.execPath, startTime: "2026-09-20T00:00:00.000Z", parentPid: 101 });
    assert.equal(inspectRestartedProcessOwnership(markerPath, 202, { aliveReader: () => true, identityReader: identity }), OWNERSHIP.OWNED);
    assert.equal(inspectHandoffProcessOwnership(markerPath, 202, { aliveReader: () => true, identityReader: identity }), OWNERSHIP.OWNED);
    assert.equal(inspectHandoffProcessOwnership(markerPath, 202, { aliveReader: () => true, identityReader: () => ({ ...identity(), startTime: "other" }) }), OWNERSHIP.NOT_OWNED);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});
