import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import { spawnSync } from "node:child_process";
import test from "node:test";
import { getProcessRunnerState, resetProcessRunnerState, runProcess } from "../support/process-runner.mjs";

test("runProcess returns a bounded timeout when the child never closes", async () => {
  resetProcessRunnerState();
  const child = new EventEmitter();
  child.pid = 31415;
  child.kill = () => true;
  const started = Date.now();
  const code = await runProcess("owned-test-process", [], {
    spawnImpl: () => child,
    timeoutMs: 5,
    timeoutCleanupMs: 20,
    killProcessTreeImpl: () => false,
    waitForExitImpl: async () => false,
  });
  assert.equal(code, 124);
  assert.ok(Date.now() - started < 1000);
  assert.equal(getProcessRunnerState().cleanupComplete, false);
});

test("close before cleanup confirmation cannot expose a successful cleanup state", async () => {
  resetProcessRunnerState();
  const child = new EventEmitter();
  child.pid = 31417;
  child.kill = () => false;
  const code = await runProcess("fake-race", [], {
    spawnImpl: () => child, timeoutMs: 5, timeoutCleanupMs: 30,
    killProcessTreeImpl: () => { setImmediate(() => child.emit("close", 1, null)); return false; },
    waitForExitImpl: async () => { await new Promise(resolve => setTimeout(resolve, 60)); return false; },
  });
  assert.equal(code, 124);
  assert.equal(getProcessRunnerState().cleanupComplete, false);
  const state = getProcessRunnerState();
  child.emit("close", 0, null);
  await new Promise(resolve => setTimeout(resolve, 70));
  assert.deepEqual(getProcessRunnerState(), state);
});

test("timeout detaches real child pipes so the failed runner can exit", () => {
  const moduleUrl = new URL("../support/process-runner.mjs", import.meta.url).href;
  const script = `import {runProcess} from ${JSON.stringify(moduleUrl)};
    const code = await runProcess(process.execPath, ['-e', 'setTimeout(()=>{}, 2500)'], {
      timeoutMs:10, timeoutCleanupMs:30, stdio:'pipe',
      killProcessTreeImpl:()=>false, waitForExitImpl:async()=>false
    }); process.exitCode=code;`;
  const started = Date.now();
  const result = spawnSync(process.execPath, ["--input-type=module", "-e", script], { timeout: 2000, stdio: "ignore", windowsHide: true });
  assert.equal(result.error, undefined);
  assert.equal(result.status, 124);
  assert.ok(Date.now() - started < 2000);
});

test("runProcess preserves a normal close result", async () => {
  resetProcessRunnerState();
  const child = new EventEmitter();
  child.pid = 31416;
  child.kill = () => true;
  const code = await runProcess("owned-test-process", [], {
    spawnImpl: () => {
      setImmediate(() => child.emit("close", 0, null));
      return child;
    },
    timeoutMs: 100,
  });
  assert.equal(code, 0);
  assert.equal(getProcessRunnerState().cleanupComplete, true);
});
