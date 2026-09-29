import { runtime, assert, fs, path, json, boot, target, report } from "./finite-common.mjs";
let evidence;
try {
  await boot();
  const owned = await target("scheduled-target");
  const user = await runtime.createUserBinding(owned.script.id, "scheduled-user");
  const trigger = new Date(Math.ceil((Date.now() + 3000) / 60000) * 60000);
  const time = `${String(trigger.getHours()).padStart(2,"0")}:${String(trigger.getMinutes()).padStart(2,"0")}`;
  const queue = await json("POST", "api/queues", { name: "scheduled-queue", autoRunMode: "scheduled",
    tasks: [{ scriptInstanceId: owned.script.id, index: 0 }],
    timeSets: [{ enabled: true, days: [trigger.getDay()], time }] });
  console.log(`Real Scheduler occurrence: ${trigger.toISOString()}`);
  const record = await runtime.waitForHistory(owned.script.id, Math.min(85000, trigger.getTime() - Date.now() + 15000));
  assert.ok(record, "Real timer did not persist a run");
  assert.equal(record.status, "success", JSON.stringify(record));
  assert.equal(record.queueId, queue.id); assert.equal(record.userId, user.id); assert.equal(record.mode, "auto");
  assert.equal(JSON.parse(fs.readFileSync(owned.effect)).label, "scheduled-target");
  await runtime.stopRuntime();
  const state = JSON.parse(fs.readFileSync(path.join(runtime.runtimeDir, ".nxp/state/scheduler-state.json"), "utf8").replace(/^\uFEFF/, ""));
  runtime.startRuntime(["service"]); await runtime.waitForService(null, 5000);
  const history = await json("GET", "api/history?days=1&limit=100");
  assert.equal(history.records.filter(item => item.scriptInstanceId === owned.script.id).length, 1);
  evidence = { trigger: trigger.toISOString(), queueId: queue.id, userId: user.id, record, schedulerState: state,
    real: ["minute Scheduler", "queue admission", "execution", "history", "restart replay fence"],
    substituted: ["owned external target"] };
} finally { await runtime.stopRuntime(); }
report("H-E04", evidence);
