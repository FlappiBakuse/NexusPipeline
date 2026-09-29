import { ownsProcess } from "../support/test-runtime.mjs";
import { killProcessTree } from "../support/windows-process.mjs";
import { runtime, assert, fs, path, json, boot, target, settled, report } from "./finite-common.mjs";
const operations = [];
let recovery;
try {
  let host = await boot();
  const configuration = path.join(runtime.runtimeDir, "account-config");
  fs.mkdirSync(configuration);
  const file = path.join(configuration, "account.json"), original = '{"owner":"ORIGINAL"}';
  fs.writeFileSync(file, original);
  const owned = await target("config-target", { configPath: configuration });
  const mode = path.join(owned.directory, "mode"); fs.writeFileSync(mode, "hold");
  fs.writeFileSync(path.join(owned.directory, "worker.mjs"), `import fs from 'node:fs';
const config=fs.readFileSync(${JSON.stringify(file)},'utf8');
fs.writeFileSync(${JSON.stringify(owned.effect)},JSON.stringify({pid:process.pid,config}));
const mode=fs.readFileSync(${JSON.stringify(mode)},'utf8');
if(mode==='hold')setInterval(()=>{},1000);else process.exitCode=mode==='fail'?1:0;`);
  const users = [await runtime.createUserBinding(owned.script.id, "A"), await runtime.createUserBinding(owned.script.id, "B")];
  const stores = users.map(user => path.join(runtime.runtimeDir, "data", owned.script.id, user.id, "store/account.json"));
  const edit = (index, action, mode = "normal") => json("POST", `api/users/${users[index].id}/bindings/${owned.script.id}/edit-config`, { action, mode });
  for (let index = 0; index < 2; index++) {
    await edit(index, "start", "fresh");
    fs.mkdirSync(configuration, { recursive: true });
    fs.writeFileSync(file, JSON.stringify({ owner: `${index ? "B" : "A"}_ONLY` }));
    await edit(index, "done");
    assert.equal(fs.readFileSync(file, "utf8"), original);
    assert.equal(JSON.parse(fs.readFileSync(stores[index])).owner, `${index ? "B" : "A"}_ONLY`);
    operations.push(`${index ? "B" : "A"} save`);
  }
  for (let index = 0; index < 2; index++) {
    const before = fs.readFileSync(stores[index]);
    await edit(index, "start");
    assert.equal(JSON.parse(fs.readFileSync(file)).owner, `${index ? "B" : "A"}_ONLY`);
    fs.writeFileSync(file, '{"owner":"DISCARD"}'); await edit(index, "cancel");
    assert.deepEqual(fs.readFileSync(stores[index]), before); assert.equal(fs.readFileSync(file, "utf8"), original);
    operations.push(`${index ? "B" : "A"} cancel`);
  }
  for (const [index, behavior] of [[0,"success"],[1,"success"],[0,"fail"],[1,"success"]]) {
    fs.writeFileSync(mode, behavior); fs.rmSync(owned.effect, { force: true });
    const definition = { ...owned.script, judgeScript: `console.log(JSON.stringify({status:'${behavior === "fail" ? "failed" : "success"}',reason:'owned-config'}));` };
    await json("PUT", `api/scripts/${owned.script.id}`, definition);
    const start = await json("POST", "api/dispatch/script", { scriptId: owned.script.id, userName: users[index].name });
    const result = await settled(start.runId);
    assert.equal(result.records[0].status, behavior === "fail" ? "failed" : "success");
    assert.equal(result.records[0].userId, users[index].id);
    assert.equal(JSON.parse(JSON.parse(fs.readFileSync(owned.effect)).config).owner, `${index ? "B" : "A"}_ONLY`);
    assert.equal(fs.readFileSync(file, "utf8"), original); operations.push({ user: users[index].id, behavior, runId: start.runId });
  }
  fs.writeFileSync(mode, "hold"); fs.rmSync(owned.effect, { force: true });
  await edit(0, "start");
  const journal = path.join(runtime.runtimeDir, "data", owned.script.id, users[0].id, ".session");
  assert.ok(fs.existsSync(journal));
  assert.equal(await runtime.waitFor(() => fs.existsSync(owned.effect), 3000, 25), true);
  const mark = JSON.parse(fs.readFileSync(journal, "utf8").replace(/^\uFEFF/, ""));
  assert.equal(mark.SessionPhase, "edit");
  assert.equal(JSON.parse(fs.readFileSync(file)).owner, "A_ONLY");
  assert.equal(ownsProcess(runtime.runMarkerPath, host.pid), true);
  assert.equal(killProcessTree(host.pid), true);
  assert.equal(await runtime.waitFor(() => host.exitCode !== null || host.signalCode !== null, 3000, 25), true);
  host = runtime.startRuntime(["service"]); await runtime.waitForService(null, 5000);
  assert.equal(fs.readFileSync(file, "utf8"), original);
  assert.equal(fs.existsSync(journal), false);
  for (let index = 0; index < 2; index++) assert.equal(JSON.parse(fs.readFileSync(stores[index])).owner, `${index ? "B" : "A"}_ONLY`);
  await runtime.stopRuntime(); runtime.startRuntime(["service"]); await runtime.waitForService(null, 5000);
  assert.equal(fs.readFileSync(file, "utf8"), original);
  recovery = { phase: mark.SessionPhase, recovered: true, idempotentRestart: true };
} finally { await runtime.stopRuntime(); }
report("H-E02", { operations, recovery, real: ["configuration edit and snapshots", "directory swap", "owned crash recovery", "execution and history"],
  substituted: ["owned external target and synthetic account files"] });
