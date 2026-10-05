import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { createHash, randomUUID } from "node:crypto";
import { resolveTestRunRoot } from "../support/test-runtime.mjs";
import { readProcessIdentity } from "../support/windows-process.mjs";

process.env.NEXUS_SYSTEM_RUNTIME_NAME = "update-runtime";
const root = resolveTestRunRoot(path.resolve(import.meta.dirname, "../.."), process.env.NEXUS_TEST_RUN_ID);
process.env.NEXUS_TEST_HOST_EXIT_FILE = path.join(root, "update-runtime", ".nxp", "test-host.exit");
const runtime = await import("./runtime-helper.mjs");
const hash = file => createHash("sha256").update(fs.readFileSync(file)).digest("hex");
const readJson = file => JSON.parse(fs.readFileSync(file, "utf8").replace(/^\uFEFF/u, ""));

test("current update worker commits only after actual services are ready and preserves user files", async () => {
  await runtime.prepareRuntime();
  const settings = path.join(runtime.runtimeDir, "config/settings.json");
  fs.writeFileSync(settings, JSON.stringify({ ...JSON.parse(fs.readFileSync(settings)), UpdateCheckEnabled: false }));
  const user = path.join(runtime.runtimeDir, "data", "preserved.bin");
  fs.mkdirSync(path.dirname(user), { recursive: true });
  fs.writeFileSync(user, Buffer.from([0, 255, 13, 10, 42]));
  const original = hash(user), beforeImage = hash(runtime.runtimeExe);
  const staging = path.join(runtime.runtimeDir, ".nxp-update/staging/0.16.15.g1");
  fs.mkdirSync(path.join(staging, "wwwroot"), { recursive: true });
  fs.copyFileSync(runtime.runtimeExe, path.join(staging, "nexus-pipeline.exe"));
  fs.writeFileSync(path.join(staging, "wwwroot/index.html"), "current update payload");
  fs.writeFileSync(path.join(staging, "README.md"), "current update readme");
  const journal = path.join(runtime.runtimeDir, ".nxp-update/task.json");
  fs.writeFileSync(journal, JSON.stringify({ Mode: "apply", Version: "0.16.15", StagedDir: staging,
    Phase: "ApplyRequested", CreatedAt: new Date().toISOString() }));
  const plan = path.join(root, "update-policy-plan.json");
  fs.writeFileSync(plan, JSON.stringify({ RunId: runtime.runId, Exchanges: [{ Id: "fresh-worker-policy", Method: "GET",
    Url: "https://raw.githubusercontent.com/FlappiBakuse/NexusPipeline/main/update-policy.json", Status: 200,
    BodyBase64: Buffer.from(JSON.stringify({ schemaVersion: 1, repository: "FlappiBakuse/NexusPipeline", barriers: [] })).toString("base64"), ContentType: "application/json" }] }));
  const worker = path.join(runtime.runtimeDir, `.nxp-update-worker-${randomUUID().replaceAll("-", "")}.exe`);
  fs.copyFileSync(runtime.runtimeExe, worker);
  let status;
  try {
    runtime.startUpdateWorker(worker, ["apply-update", "--staged", staging, "--web"], { NEXUS_TEST_HTTP_PLAN: plan });
    assert.equal(await runtime.waitFor(async () => {
      try { status = await (await runtime.api("GET", "api/status")).json(); return status.ready === true && !fs.existsSync(journal); }
      catch { return false; }
    }, 20000, 100), true, runtime.runtimeDiagnostic());
    assert.equal(fs.readFileSync(path.join(runtime.runtimeDir, "wwwroot/index.html"), "utf8"), "current update payload");
    assert.equal(hash(runtime.runtimeExe), beforeImage);
    assert.equal(hash(user), original);
    assert.equal(fs.existsSync(staging), false);
    assert.equal(fs.existsSync(worker), false);
    assert.equal(fs.existsSync(path.join(runtime.runtimeDir, ".nxp-backup")), false);
    const directory = path.join(runtime.runtimeDir, ".nxp/state/updates");
    const results = fs.readdirSync(directory).filter(file => file.endsWith(".result.json"));
    assert.equal(results.length, 1);
    const result = readJson(path.join(directory, results[0]));
    assert.equal(result.Succeeded, true);
    const receipt = readJson(path.join(directory, results[0].replace(".result.json", ".startup.json")));
    assert.equal(receipt.TransactionId, result.TransactionId);
    assert.equal(receipt.ImageHash, beforeImage);
    const observed = readProcessIdentity(receipt.Identity.Pid);
    assert.equal(path.resolve(observed.executablePath).toLowerCase(), path.resolve(runtime.runtimeExe).toLowerCase());
  } finally { await runtime.stopRuntime(); }
});

test("failed policy proof aborts only its exited worker and cannot repeat the apply loop", async () => {
  await runtime.prepareRuntime();
  const settings = path.join(runtime.runtimeDir, "config/settings.json");
  fs.writeFileSync(settings, JSON.stringify({ ...JSON.parse(fs.readFileSync(settings)), UpdateCheckEnabled: false }));
  const beforeImage = hash(runtime.runtimeExe);
  const user = path.join(runtime.runtimeDir, "history", "old-format.json");
  fs.mkdirSync(path.dirname(user), { recursive: true });
  fs.writeFileSync(user, '{"old":"retained"}\r\n');
  const original = hash(user);
  const staging = path.join(runtime.runtimeDir, ".nxp-update/staging/0.17.0.g1");
  fs.mkdirSync(staging, { recursive: true });
  fs.copyFileSync(runtime.runtimeExe, path.join(staging, "nexus-pipeline.exe"));
  const journal = path.join(runtime.runtimeDir, ".nxp-update/task.json");
  fs.writeFileSync(journal, JSON.stringify({ Mode: "apply", Version: "0.17.0", StagedDir: staging,
    Phase: "ApplyRequested", CreatedAt: new Date().toISOString() }));
  const plan = path.join(root, "failed-update-policy-plan.json");
  fs.writeFileSync(plan, JSON.stringify({ RunId: runtime.runId, Exchanges: [{ Id: "failed-fresh-policy", Method: "GET",
    Url: "https://raw.githubusercontent.com/FlappiBakuse/NexusPipeline/main/update-policy.json", Status: 503,
    BodyBase64: Buffer.from("unavailable").toString("base64"), ContentType: "text/plain" }] }));
  const worker = path.join(runtime.runtimeDir, `.nxp-update-worker-${randomUUID().replaceAll("-", "")}.exe`);
  fs.copyFileSync(runtime.runtimeExe, worker);
  try {
    runtime.startUpdateWorker(worker, ["apply-update", "--staged", staging, "--web"], { NEXUS_TEST_HTTP_PLAN: plan });
    assert.equal(await runtime.waitFor(async () => {
      try { return (await (await runtime.api("GET", "api/status")).json()).ready === true && !fs.existsSync(journal); }
      catch { return false; }
    }, 10000, 100), true, runtime.runtimeDiagnostic());
    assert.equal(hash(runtime.runtimeExe), beforeImage);
    assert.equal(hash(user), original);
    assert.equal(fs.existsSync(staging), false);
    assert.equal(fs.existsSync(worker), false);
    assert.equal(fs.existsSync(path.join(runtime.runtimeDir, ".nxp-backup")), false);
    const results = fs.readdirSync(path.join(runtime.runtimeDir, ".nxp/state/updates")).filter(file => file.endsWith(".result.json"))
      .map(file => readJson(path.join(runtime.runtimeDir, ".nxp/state/updates", file))).filter(result => result.Version === "0.17.0");
    assert.equal(results.length, 1);
    assert.equal(results[0].Succeeded, false);
    assert.equal(results[0].Code, "policy-unavailable");
    const exchanges = fs.readFileSync(plan + ".receipts.jsonl", "utf8").trim().split("\n").map(JSON.parse);
    assert.equal(exchanges.length, 1);
    assert.equal(exchanges[0].matched, true);
  } finally { await runtime.stopRuntime(); }
});
