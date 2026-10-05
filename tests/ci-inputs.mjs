import fs from "node:fs";
import path from "node:path";
import {execFileSync} from "node:child_process";
import {createHash} from "node:crypto";

const repositories = ["FlappiBakuse/NexusPipeline", "FlappiBakuse/NexusPipeline-Plugins"];
const python = process.platform === "win32" ? "python" : "python3";
const git = (root, ...args) => execFileSync("git", ["-C", root, ...args], {encoding:"utf8", windowsHide:true, timeout:8000}).trim();
const fileDigest = file => createHash("sha256").update(fs.readFileSync(file,"utf8").replaceAll("\r\n","\n")).digest("hex");

export function defaultPartner(root) {
  const input = JSON.parse(fs.readFileSync(path.join(root, "plugins.lock.json"), "utf8"));
  if (Object.keys(input).sort().join(",") !== "ref,repository,schemaVersion" || input.schemaVersion !== 1
      || input.repository !== repositories[1] || input.ref !== "main") throw new Error("Invalid default Plugins input");
  return input;
}

export function resolvePair(root, repository, identity) {
  return JSON.parse(execFileSync(python, [path.join(root,"tests/ci_inputs.py"),
    "--repository",repository,"--pr",String(identity.prNumber),"--base",identity.base,"--head",identity.head,
    "--tested",git(root,"rev-parse","HEAD")], {encoding:"utf8",windowsHide:true,timeout:100000}));
}

export function validatePairCheckout(root, file, plan, partnerRoot) {
  const pair = JSON.parse(execFileSync(python,[path.join(root,"tests/ci_inputs.py"),"--plan",file],
    {encoding:"utf8",windowsHide:true,timeout:8000}));
  if ((plan.inputMode ?? "default") !== (pair ? "paired" : "default")) throw new Error("Input mode differs from pair");
  if (!pair) return;
  const index = plan.repository === "Host" ? 0 : 1;
  const own = pair.sources[index], partner = pair.sources[1-index];
  if (own.repository !== repositories[index] || own.prNumber !== plan.prNumber || own.baseSha !== plan.baseSha
      || own.headSha !== plan.headSha || own.testedSha !== plan.testedSha || partner.testedSha !== plan.partnerSha)
    throw new Error("Pair differs from scope identity");
  for (const [directory, source] of [[root,own], ...(partnerRoot ? [[partnerRoot,partner]] : [])]) {
    const lock = source.repository === repositories[0] ? "plugins.lock.json" : "tests/inputs.lock.json";
    if (git(directory,"rev-parse","HEAD") !== source.testedSha || git(directory,"rev-parse","HEAD^{tree}") !== source.treeSha
        || git(directory,"status","--porcelain") || fileDigest(path.join(directory,"tests/policy.json")) !== source.policyDigest
        || fileDigest(path.join(directory,lock)) !== source.inputLockDigest)
      throw new Error("Pair checkout, policy or input lock differs");
  }
}

export function validateDefaultHost(root, sha) {
  return execFileSync(python,[path.join(root,"tests/ci_inputs.py"),"--default-host-sha",sha],
    {encoding:"utf8",windowsHide:true,timeout:15000}).trim();
}
