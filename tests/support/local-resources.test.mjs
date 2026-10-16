import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync, spawn } from "node:child_process";
import { fileURLToPath } from "node:url";
import { stageWorkspace } from "./workspace.mjs";
import { Budget } from "./budget.mjs";
import { scheduleHeavy, reserveLocalResources } from "./local-resources.mjs";
import { ensureNpmInputs, npmIdentity } from "./npm-inputs.mjs";
import { preparedArtifact } from "./prepared-artifact.mjs";

const repository = fileURLToPath(new URL("../../", import.meta.url));
function fixture(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "nxp-resources-"));
  t.after(() => fs.rmSync(root, { recursive: true, force: true })); return root;
}
function profile(t) {
  const old = process.env.NEXUS_TEST_LOW_RESOURCE;
  process.env.NEXUS_TEST_LOW_RESOURCE = "1";
  t.after(() => { if (old === undefined) delete process.env.NEXUS_TEST_LOW_RESOURCE; else process.env.NEXUS_TEST_LOW_RESOURCE = old; });
}
function source(t) {
  const root = fixture(t), repo = path.join(root, "source");
  execFileSync("git", ["init", "--quiet", repo]);
  const objects = execFileSync("git", ["rev-parse", "--path-format=absolute", "--git-path", "objects"], { cwd: repository, encoding: "utf8" }).trim();
  fs.writeFileSync(path.join(repo, ".git/objects/info/alternates"), objects.replaceAll("\\", "/") + "\n");
  const head = execFileSync("git", ["rev-parse", "HEAD"], { cwd: repository, encoding: "utf8" }).trim();
  execFileSync("git", ["update-ref", "HEAD", head], { cwd: repo });
  execFileSync("git", ["read-tree", "--empty"], { cwd: repo });
  fs.writeFileSync(path.join(repo, "example.cs"), "first\r\n");
  fs.writeFileSync(path.join(repo, ".gitignore"), "bin/\n");
  return { root, repo, stage: () => stageWorkspace(repo, path.join(root, "artifacts"), new Budget("fixture", null), "Host", { dotnetRequired: false }) };
}

test("incremental source sync preserves build outputs and writes only changed bytes", t => {
  profile(t); const f = source(t);
  const first = f.stage(); first.release();
  const file = path.join(first.directory, "example.cs"), timestamp = fs.statSync(file).mtimeMs;
  fs.mkdirSync(path.join(first.directory, "bin")); fs.writeFileSync(path.join(first.directory, "bin/result"), "compiled");
  const repeated = f.stage(); repeated.release();
  assert.equal(repeated.staging.copiedBytes, 0); assert.equal(fs.statSync(file).mtimeMs, timestamp);
  fs.writeFileSync(path.join(f.repo, "example.cs"), "second\r\n");
  const changed = f.stage(); changed.release();
  assert.equal(changed.directory, first.directory); assert.notEqual(changed.fingerprint, first.fingerprint);
  assert.equal(changed.staging.copiedBytes, 8); assert.equal(changed.staging.copiedFiles, 1);
  assert.equal(fs.readFileSync(path.join(first.directory, "bin/result"), "utf8"), "compiled");
  fs.unlinkSync(path.join(f.repo, "example.cs"));
  const removed = f.stage(); removed.release();
  assert.equal(removed.staging.removedFiles, 1); assert.equal(fs.existsSync(file), false);
});

test("dependency graph changes isolate outputs and unknown staged edits fail closed", t => {
  profile(t); const f = source(t);
  const first = f.stage(); first.release();
  fs.writeFileSync(path.join(f.repo, "package-lock.json"), "{}");
  const changed = f.stage(); changed.release(); assert.notEqual(changed.directory, first.directory);
  fs.writeFileSync(path.join(changed.directory, "example.cs"), "unknown");
  assert.throws(f.stage, /changed outside/);
  assert.equal(fs.readFileSync(path.join(changed.directory, "example.cs"), "utf8"), "unknown");
  assert.ok(fs.existsSync(path.join(changed.directory, ".nxp-test-lease.json")));
});

test("an occupied workspace and linked source cannot be reused", t => {
  profile(t); const f = source(t); const first = f.stage();
  assert.throws(f.stage, /EEXIST/); first.release();
  const outside = path.join(f.root, "outside"); fs.mkdirSync(outside);
  fs.symlinkSync(outside, path.join(first.directory, "linked"), process.platform === "win32" ? "junction" : "dir");
  fs.mkdirSync(path.join(f.repo, "linked")); fs.writeFileSync(path.join(f.repo, "linked/new.cs"), "new");
  assert.throws(f.stage, /Linked/); assert.deepEqual(fs.readdirSync(outside), []);
});

test("unregistered source additions never enter the incremental build", t => {
  profile(t); const f = source(t), first = f.stage(); first.release();
  fs.writeFileSync(path.join(first.directory, "injected.cs"), "unknown");
  assert.throws(f.stage, /Unowned staged file/);
  assert.equal(fs.readFileSync(path.join(first.directory, "injected.cs"), "utf8"), "unknown");
});

test("heavy commands queue, keep failures and cancel before spawning", async () => {
  let active = 0, peak = 0;
  const controller = new AbortController();
  const options = { env: { NEXUS_TEST_LOW_RESOURCE: "1" } };
  const execute = async (command, args, opts) => {
    peak = Math.max(peak, ++active); assert.ok(args.includes("-m:1")); assert.equal(opts.env.DOTNET_PROCESSOR_COUNT, "2");
    await new Promise(resolve => setTimeout(resolve, 30)); active--; return 23;
  };
  const first = scheduleHeavy("dotnet", ["build", "test.csproj"], options, execute);
  const second = scheduleHeavy("dotnet", ["build", "test.csproj"], options, execute);
  const cancelled = scheduleHeavy("npm.cmd", ["ci"], { ...options, signal: controller.signal }, () => assert.fail("cancelled work spawned"));
  controller.abort();
  assert.deepEqual(await Promise.all([first, second, cancelled]), [23, 23, 130]); assert.equal(peak, 1);
});

test("resource reservations serialize independent processes", async t => {
  const root = fixture(t), events = path.join(root, "events");
  const module = new URL("./local-resources.mjs", import.meta.url).href;
  const script = `import fs from 'node:fs'; import {reserveLocalResources} from ${JSON.stringify(module)};
    const release=await reserveLocalResources(); fs.appendFileSync(process.env.EVENTS,'start '+process.pid+'\\n');
    await new Promise(r=>setTimeout(r,100)); fs.appendFileSync(process.env.EVENTS,'end '+process.pid+'\\n'); release();`;
  const run = () => new Promise((resolve, reject) => {
    const env = { ...process.env, NEXUS_TEST_LOW_RESOURCE: "1", NEXUS_TEST_RESOURCE_ROOT: root, EVENTS: events };
    delete env.NEXUS_TEST_RESERVATION;
    const child = spawn(process.execPath, ["--input-type=module", "-e", script], { env, windowsHide: true, stdio: "pipe" });
    let error = ""; child.stderr.on("data", bytes => error += bytes); child.on("error", reject);
    child.on("close", code => code ? reject(new Error(error)) : resolve());
  });
  await Promise.all([run(), run()]);
  const lines = fs.readFileSync(events, "utf8").trim().split("\n");
  assert.match(lines[0], /^start/); assert.equal(lines[1], lines[0].replace("start", "end"));
  assert.match(lines[2], /^start/); assert.equal(lines[3], lines[2].replace("start", "end"));
  assert.equal(fs.existsSync(path.join(root, "heavy-task.lock")), false);
});

test("stale reservation is preserved and cancelled wait never takes the lock", async t => {
  const root = fixture(t), lock = path.join(root, "heavy-task.lock");
  const env = { NEXUS_TEST_LOW_RESOURCE: "1", NEXUS_TEST_RESOURCE_ROOT: root };
  fs.writeFileSync(lock, JSON.stringify({ pid: -1, token: "interrupted" }));
  await assert.rejects(reserveLocalResources({ env }), /Interrupted/); assert.ok(fs.existsSync(lock));
  const controller = new AbortController(); controller.abort();
  await assert.rejects(reserveLocalResources({ env, signal: controller.signal }), /cancelled/);
});

test("dependency receipts reuse installs, invalidate locks and never cache failure", async t => {
  const root = fixture(t); fs.writeFileSync(path.join(root, "package.json"), '{}');
  fs.writeFileSync(path.join(root, "package-lock.json"), '{}'); let calls = 0;
  const install = async () => { calls++; fs.mkdirSync(path.join(root, "node_modules"), { recursive: true }); fs.writeFileSync(path.join(root, "node_modules/.package-lock.json"), '{}'); return 0; };
  assert.equal(await ensureNpmInputs(root, install), 0); assert.equal(await ensureNpmInputs(root, install), 0); assert.equal(calls, 1);
  fs.writeFileSync(path.join(root, "package-lock.json"), '{"changed":true}');
  assert.equal(await ensureNpmInputs(root, async () => 23), 23);
  await ensureNpmInputs(root, install); assert.equal(calls, 2);
  await ensureNpmInputs(root, install, ["--workspaces=false"]); assert.equal(calls, 3);
});

test("moving the download cache does not invalidate installed dependencies", t => {
  const root = fixture(t), old = process.env.npm_config_cache;
  t.after(() => { if (old === undefined) delete process.env.npm_config_cache; else process.env.npm_config_cache = old; });
  const identity = npmIdentity(root); process.env.npm_config_cache = path.join(root, "downloads");
  assert.equal(npmIdentity(root), identity);
});

test("prepared artifacts verify exact bytes, identities, and incomplete builds", async t => {
  const root = fixture(t); let calls = 0;
  const build = async output => { calls++; fs.mkdirSync(output); fs.writeFileSync(path.join(output, "image"), "payload"); return 0; };
  const first = await preparedArtifact(root, { source: "a", mode: "test" }, build);
  const second = await preparedArtifact(root, { source: "a", mode: "test" }, build);
  assert.equal(second.cacheHit, true); assert.equal(calls, 1);
  await preparedArtifact(root, { source: "b", mode: "test" }, build); assert.equal(calls, 2);
  fs.writeFileSync(path.join(first.output, "image"), "tampered");
  await assert.rejects(preparedArtifact(root, { source: "a", mode: "test" }, build), /changed/);
  const failed = await preparedArtifact(root, { source: "failed" }, async () => 23); assert.equal(failed.code, 23);
  await assert.rejects(preparedArtifact(root, { source: "failed" }, build), /ENOENT/); assert.equal(calls, 2);
});

test("concurrent consumers prepare an identical artifact only once", async t => {
  const root = fixture(t); let calls = 0;
  const build = async output => {
    calls++; fs.mkdirSync(output); await new Promise(resolve => setTimeout(resolve, 20));
    fs.writeFileSync(path.join(output, "image"), "payload"); return 0;
  };
  const [first, second] = await Promise.all([preparedArtifact(root, "same", build), preparedArtifact(root, "same", build)]);
  assert.equal(calls, 1); assert.equal(first.output, second.output);
});
