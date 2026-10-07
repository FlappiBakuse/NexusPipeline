import assert from "node:assert/strict";
import test from "node:test";
import { EventEmitter } from "node:events";
import { Budget } from "./budget.mjs";
import { runProcess, resetProcessRunnerState, getProcessRunnerState } from "./process-runner.mjs";

test("a child process inherits elapsed preparation and cannot reset its parent work",()=>{
  let time=1000;
  const budget=new Budget("child",180000,{qualificationMs:150000,reserveMs:50000,clock:()=>time,inheritedWorkMs:1200,inheritedHardMs:6200});
  time=2200;assert.equal(budget.remainingMs(),0);assert.equal(budget.remainingMs({cleanup:true}),5000);
  assert.throws(()=>budget.child("late",150000));
});

test("child work and cleanup stay inside the original parent deadline", () => {
  let time = 0;
  const parent = new Budget("daily", 180_000, { reserveMs: 10_000, clock: () => time });
  time = 150_000;
  const plugin = parent.child("ordinary", 30_000, 5_000);
  assert.equal(plugin.remainingMs(), 20_000);
  time = 170_000;
  assert.equal(plugin.remainingMs(), 0);
  assert.equal(plugin.remainingMs({ cleanup: true }), 10_000);
  assert.throws(() => plugin.child("late", 30_000));
});

test("an exhausted budget never launches work", async () => {
  let time = 0;
  const budget = new Budget("test", 100, { clock: () => time });
  time = 100;
  const result = await runProcess("unused", [], { budget, spawnImpl: () => assert.fail("started after deadline") });
  assert.equal(result, 5);
});

test("qualification stops new work at 150 seconds while preserving bounded cleanup", () => {
  let time = 0;
  const budget = new Budget("gate", 180_000, { qualificationMs: 150_000,
    reserveMs: 10_000, clock: () => time });
  time = 150_000;
  budget.assertWithinBudget();
  assert.equal(budget.remainingMs(), 0);
  assert.equal(budget.remainingMs({ cleanup: true }), 30_000);
  time = 150_001;
  assert.throws(() => budget.assertWithinBudget());
  time = 180_000;
  assert.equal(budget.remainingMs({ cleanup: true }), 0);
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

test("child cleanup uses the parent hard window without restarting work or qualification", () => {
  let time = 0;
  const parent = new Budget("batch", 180000, { reserveMs: 50000, qualificationMs: 150000, clock: () => time });
  time = 120000;
  const child = parent.child("last plugin", 150000, 5000);
  assert.equal(child.remainingMs(), 10000);
  time = 130000;
  assert.equal(child.remainingMs(), 0);
  assert.equal(child.remainingMs({cleanup:true}), 50000);
  assert.throws(() => child.child("new work", 30000));
  time = 150000; child.assertWithinBudget();
  time = 150001; assert.throws(() => child.assertWithinBudget());
  time = 180000; assert.equal(child.remainingMs({cleanup:true}), 0);
});

test("unlimited timing records elapsed work and imposes no child deadline",()=>{
 let time=0;const root=new Budget("invocation",null,{clock:()=>time});
 const child=root.child("desktop",null);time=3_600_000;
 assert.equal(root.elapsedMs,3_600_000);assert.equal(child.remainingMs(),Infinity);
 root.check();child.assertWithinBudget();
});

test("unlimited command preserves the real child result past a supplied execution timeout",async()=>{
 const {runProcess}=await import("./process-runner.mjs");
 assert.equal(await runProcess(process.execPath,["-e","setTimeout(()=>process.exit(7),40)"],{
  budget:new Budget("desktop build",null),timeoutMs:1
 }),7);
});
