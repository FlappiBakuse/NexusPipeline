import assert from "node:assert/strict";
import test from "node:test";
import { EventEmitter } from "node:events";
import { Budget } from "./budget.mjs";
import { runProcess, resetProcessRunnerState, getProcessRunnerState } from "./process-runner.mjs";

test("child work and cleanup stay inside the original parent deadline", () => {
  let time = 0;
  const parent = new Budget("daily", 180_000, { reserveMs: 10_000, clock: () => time });
  time = 150_000;
  const plugin = parent.child("ordinary", 30_000, 5_000);
  assert.equal(plugin.remainingMs(), 15_000);
  time = 165_000;
  assert.equal(plugin.remainingMs(), 0);
  assert.equal(plugin.remainingMs({ cleanup: true }), 5_000);
  assert.throws(() => plugin.child("late", 30_000));
});

test("an exhausted budget never launches work", async () => {
  let time = 0;
  const budget = new Budget("test", 100, { clock: () => time });
  time = 100;
  const result = await runProcess("unused", [], { budget, spawnImpl: () => assert.fail("started after deadline") });
  assert.equal(result, 5);
});

test("nonzero child exit is preserved", async () => {
  const child = new EventEmitter();
  const result = runProcess("fixture", [], { spawnImpl: () => child, timeoutMs: 1_000 });
  child.emit("close", 23, null);
  assert.equal(await result, 23);
});

test("timeout with unconfirmed cleanup cannot become success", async () => {
  resetProcessRunnerState();
  const child = Object.assign(new EventEmitter(), {
    pid: 1, exitCode: null, signalCode: null, unref() {}, kill() { return false; },
  });
  const code = await runProcess("fixture", [], {
    spawnImpl: () => child,
    timeoutMs: 10,
    timeoutCleanupMs: 20,
    killProcessTreeImpl: async () => false,
    waitForExitImpl: async () => false,
  });
  assert.equal(code, 5);
  assert.equal(getProcessRunnerState().cleanupComplete, false);
  resetProcessRunnerState();
});
