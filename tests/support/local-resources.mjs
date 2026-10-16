import fs from "node:fs";
import path from "node:path";
import { randomUUID } from "node:crypto";

export function localProfile(env = process.env) { return env.NEXUS_TEST_LOW_RESOURCE === "1"; }

function alive(pid) {
  if (!Number.isSafeInteger(pid) || pid <= 0) return false;
  try { process.kill(pid, 0); return true; } catch (error) { return error.code === "EPERM"; }
}

export async function reserveLocalResources({ env = process.env, signal, cleanup = () => true } = {}) {
  if (!localProfile(env)) return () => {};
  const queuedAt = Date.now();
  const cleanups = globalThis[Symbol.for("NexusPipeline.resourceCleanups")] ??= new Set();
  cleanups.add(cleanup);
  const root = env.NEXUS_TEST_RESOURCE_ROOT;
  if (!root || !path.isAbsolute(root)) throw new Error("Low-resource profile requires absolute NEXUS_TEST_RESOURCE_ROOT");
  for (let current = root; ; current = path.dirname(current)) {
    if (fs.existsSync(current) && fs.lstatSync(current).isSymbolicLink()) throw new Error("Linked resource root");
    if (path.dirname(current) === current) break;
  }
  fs.mkdirSync(root, { recursive: true });
  const lock = path.join(root, "heavy-task.lock");
  if (env.NEXUS_TEST_RESERVATION) {
    const owner = JSON.parse(fs.readFileSync(lock, "utf8"));
    if (owner.token !== env.NEXUS_TEST_RESERVATION || !alive(owner.pid)) throw new Error("Invalid inherited resource reservation");
    process.once("exit", () => {
      if (!cleanup()) fs.writeFileSync(path.join(root, `unsafe-${owner.token}.json`), JSON.stringify({ pid: process.pid, reason: "Child cleanup unconfirmed" }));
    });
    return () => {};
  }
  const owner = JSON.stringify({ schemaVersion: 1, pid: process.pid, token: randomUUID(), startedAt: new Date().toISOString() });
  let waiting = false, incompleteReads = 0;
  while (true) {
    if (signal?.aborted) throw Object.assign(new Error("Resource wait cancelled"), { exitCode: 130 });
    try { fs.writeFileSync(lock, owner, { flag: "wx" }); break; }
    catch (error) {
      if (error.code !== "EEXIST") throw error;
      let active;
      try { active = JSON.parse(fs.readFileSync(lock, "utf8")); }
      catch (readError) {
        if (readError.code === "ENOENT" || (readError instanceof SyntaxError && ++incompleteReads <= 3)) { await new Promise(resolve => setTimeout(resolve, 100)); continue; }
        throw readError;
      }
      if (!alive(active.pid)) throw new Error(`Interrupted resource reservation; inspect its processes and evidence before removing ${lock}`);
      if (!waiting) { console.error(`[resources] Waiting for PID ${active.pid}`); waiting = true; }
      await new Promise(resolve => setTimeout(resolve, 250));
    }
  }
  env.NEXUS_TEST_RESERVATION = JSON.parse(owner).token;
  env.NEXUS_TEST_QUEUE_MS = String(Date.now() - queuedAt);
  console.error(`[resources] acquired queueMs=${env.NEXUS_TEST_QUEUE_MS}`);
  let released = false;
  const release = () => {
    if (released) return;
    if ([...cleanups].some(check => !check()) || fs.existsSync(path.join(root, `unsafe-${JSON.parse(owner).token}.json`))) {
      console.error(`[resources] Cleanup unconfirmed; retained ${lock}`); return;
    }
    if (fs.readFileSync(lock, "utf8") !== owner) throw new Error("Resource reservation ownership changed");
    fs.unlinkSync(lock); delete env.NEXUS_TEST_RESERVATION; released = true;
  };
  process.once("exit", release);
  return release;
}

let tail = Promise.resolve();
export function scheduleHeavy(command, args, options, execute) {
  const env = options.env || process.env;
  const name = path.basename(command).toLowerCase();
  const heavy = /^(?:dotnet(?:\.exe)?|npm(?:\.cmd)?|npx(?:\.cmd)?|python(?:3)?(?:\.exe)?|iscc(?:\.exe)?)$/.test(name);
  if (!localProfile(env) || !heavy) return execute(command, args, options);
  const queuedAt = performance.now();
  const work = tail.then(() => {
    if (options.signal?.aborted) return 130;
    if ((options.budget?.remainingMs() ?? Infinity) <= 0) return options.timeoutCode ?? 5;
    const boundedArgs = name.startsWith("dotnet") && ["build", "publish", "test", "restore", "msbuild"].includes(args[0])
      ? [...args.slice(0, args.indexOf("--") < 0 ? args.length : args.indexOf("--")), "-m:1",
        ...(args.indexOf("--") < 0 ? [] : args.slice(args.indexOf("--")))] : args;
    const message = `[resources] queueMs=${Math.round(performance.now() - queuedAt)} command=${name}\n`;
    console.error(message.trimEnd()); options.onOutput?.(message);
    return execute(command, boundedArgs, { ...options, env: { ...env, DOTNET_PROCESSOR_COUNT: "2" } });
  });
  tail = work.then(() => {}, () => {});
  return work;
}
