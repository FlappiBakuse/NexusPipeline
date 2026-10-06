import {controlManifest as readControlManifest,sourceFingerprint} from "./control-inputs.mjs";
import fs from "node:fs";
import path from "node:path";
import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import { coreUnits, allocateUnits } from "./core-plan.mjs";

const SHA = /^[0-9a-f]{40}$/;
const normalize = value => value.replaceAll("\\", "/");
const hash = bytes => createHash("sha256").update(bytes).digest("hex");

export function policyDigest(bytes) {
  if (bytes.subarray(0, 3).equals(Buffer.from([0xef, 0xbb, 0xbf]))) throw new Error("Policy has a UTF-8 BOM");
  return hash(Buffer.from(bytes.toString("utf8").replaceAll("\r\n", "\n"), "utf8"));
}

export function readRegistry(root) {
  const bytes = fs.readFileSync(path.join(root, "tests/gates.json"));
  const registry = JSON.parse(bytes);
  if (registry.schemaVersion !== 1 || !["Host", "Plugins"].includes(registry.repository)
      || registry.hardTimeoutMs !== 180000 || registry.qualificationMs !== 150000) throw new Error("Invalid gate registry");
  const ids = registry.gates.map(gate => gate.id);
  if (new Set(ids).size !== ids.length || registry.gates.some(gate => !gate.id.startsWith(registry.repository.toLowerCase() + ".")
      || !/^[a-z0-9.-]+$/.test(gate.id)
      || gate.hardTimeoutMs !== 180000 || gate.qualificationMs !== 150000)) throw new Error("Duplicate or invalid gate");
  return { registry, digest: policyDigest(bytes) };
}

function git(root, args) {
  return execFileSync("git", ["-C", root, ...args], { windowsHide: true, timeout: 15000, maxBuffer: 32 * 1024 * 1024 });
}

function fullSha(root, ref) {
  const sha = git(root, ["rev-parse", "--verify", `${ref}^{commit}`]).toString("utf8").trim();
  if (!SHA.test(sha)) throw new Error(`Invalid commit: ${ref}`);
  return sha;
}

export function parseNameStatus(bytes) {
  const fields = bytes.toString("utf8").split("\0");
  if (fields.at(-1) === "") fields.pop();
  const changes = [];
  for (let index = 0; index < fields.length;) {
    const status = fields[index++];
    if (!/^(?:[ACDMRTUXB]|R\d+|C\d+)$/.test(status)) throw new Error(`Unknown git status: ${status}`);
    const oldPath = fields[index++];
    if (!oldPath) throw new Error("Truncated git diff");
    if (/^[RC]/.test(status)) {
      const newPath = fields[index++];
      if (!newPath) throw new Error("Truncated git rename/copy");
      changes.push({ status: status[0], old: normalize(oldPath), path: normalize(newPath) });
    } else changes.push({ status: status[0], path: normalize(oldPath) });
  }
  return changes;
}

export function collectChanges(root, { base, head = "HEAD", includeWorkingTree = false } = {}) {
  if (!base) throw new Error("Explicit --base commit is required");
  const baseSha = fullSha(root, base), headSha = fullSha(root, head);
  const mergeBase = git(root, ["merge-base", baseSha, headSha]).toString("utf8").trim();
  if (!SHA.test(mergeBase)) throw new Error("Cannot determine merge base");
  const changes = parseNameStatus(git(root, ["diff", "--name-status", "-z", "--find-renames", baseSha, headSha, "--"]));
  if (includeWorkingTree) {
    changes.push(...parseNameStatus(git(root, ["diff", "--name-status", "-z", "--find-renames", "HEAD", "--"])));
    changes.push(...parseNameStatus(git(root, ["diff", "--cached", "--name-status", "-z", "--find-renames", "HEAD", "--"])));
    for (const name of git(root, ["ls-files", "--others", "--exclude-standard", "-z"]).toString("utf8").split("\0").filter(Boolean)) {
      changes.push({ status: "A", path: normalize(name), untracked: true });
    }
  }
  return { baseSha, headSha, mergeBase, testedSha: fullSha(root, "HEAD"),
    dirty: Boolean(git(root,["status","--porcelain"]).toString("utf8").trim()),
    changes };
}

function allPluginNames(policy) { return Object.keys(policy.plugins).sort(); }

export function planForChanges(root, changes, registry, policy) {
  const owner = registry.repository.toLowerCase();
  const selected = new Map();
  const add = (id, reason) => {
    if (!selected.has(id)) selected.set(id, []);
    selected.get(id).push(reason);
  };
  const host = (suffix, reason) => add(`host.${suffix}`, reason);
  const plugin = (suffix, artifact, reason) => add(`plugins.plugin.${suffix}:${artifact}`, reason);
  const names = owner === "plugins" ? allPluginNames(policy) : [];
  const classifyHost = (name, status) => {
    const reason = `${status} ${name}`;
    if (name.startsWith("tests/support/") || name === "tests/batch-runner.mjs") {
      fullCore(`execution adapter: ${reason}`); return;
    }
    if (name === "src/NexusPipeline.csproj" || name.startsWith("src/NexusPipeline.Plugin.Abstractions/")
        || name.includes("JintScriptHost") || name.includes("TaskConfigDocument")) {
      host("partner-contract", reason);
      if (name === "src/NexusPipeline.csproj" || name.includes("JintScriptHost")) host("partner-jint", reason);
    }
    if (/^docs\/.*\.(md|png|jpg|svg|webp)$/i.test(name)) { host("docs", reason); return; }
    if (/^(README(?:\.en)?\.md|LICENSE(?:\.md)?|NOTICE(?:\.md)?)$/i.test(name)) {
      host("docs", reason); host("release-contract", reason); return;
    }
    if (/^(tests\/(?:ci-inputs|ci_inputs|test_ci_inputs|core-plan|batch-plan|scope-plan|scope-cli|selection-cases|ci-scope|ci-names|batch-required|test_batch_required|gate-required|test_gate_required|policy|gates|run|ci-gate|audit-jobs|test_audit_jobs|final-budget|test_final_budget)|\.github\/workflows\/(?:ci|final-budget)\.yml)/.test(name)) {
      host("ci-policy", reason); return;
    }
    if (/^(tools\/installer|tools\/host_installer|tools\/installer-languages)/.test(name)) {
      host("installer-localization", reason); host("release-contract", reason); return;
    }
    if (/^(tools\/host_release|tools\/host_candidate|\.github\/workflows\/release)/.test(name)) {
      host("release-contract", reason); return;
    }
    if (/^(tools\/(?:build_identity|embed_frontend)\.py|tools\/tests\/test_(?:build_identity|embed_frontend)\.py|tools\/schemas\/(?:build-inputs|desktop-build)\.schema\.json|tests\/fixtures\/build-identity\/)/.test(name)) {
      host("release-contract", reason); host("backend.updates-restart", reason); host("build.test-host", reason); return;
    }
    if (name.startsWith("tools/NexusPipeline.Architecture/") || name === "tests/architecture-fixture.py") {
      host("architecture.backend", reason); return;
    }
    if (name === "tests/partner-contract.py") { host("partner-contract", reason); return; }
    if (name.startsWith("desktop/") || /^(tools\/(?:application_|desktop_build|NexusPipeline.PayloadReader))/.test(name)) {
      host("release-contract", reason); host("backend.control", reason); host("backend.updates-restart", reason);
      host("build.test-host", reason); host("integration.control", reason); host("integration.desktop", reason); return;
    }
    if(name.startsWith("src/Host/Desktop/")||name.startsWith("src/Platform/Windows/Desktop")||name.startsWith("tests/desktop/")) host("integration.desktop",reason);
    if (name.startsWith("frontend/")) {
      host("frontend.typecheck", reason); host("frontend.build", reason);
      if (!/\.(css|scss|svg|png|jpg)$/.test(name)) host("frontend.state", reason);
      if (name.startsWith("frontend/scripts/architecture-check")) host("architecture.frontend", reason);
      if (name.startsWith("frontend/src/plugin-bridge/")) {
        host("architecture.frontend", reason); host("partner-contract", reason);
      }
      if (/frontend\/src\/(?:platform\/service-(?:restart|recovery)|stores\/shell|App\.vue)/.test(name))
        host("integration.restart-update", reason);
      return;
    }
    if (name.startsWith("src/") || name.startsWith("tests/NexusPipeline.Tests/")) {
      host("build.test-host", reason); host("architecture.backend", reason);
      const domain = name.match(/(?:src\/Modules\/|tests\/NexusPipeline\.Tests\/)([A-Za-z]+)/)?.[1]?.toLowerCase();
      const domains = {
        settings: ["settings-notifications", "control"], notifications: ["settings-notifications"],
        configuration: ["configuration", "execution"], plugins: ["plugins", "control"],
        updates: ["updates-restart", "control"], scheduling: ["scheduling", "execution"],
        execution: ["execution"], controlplane: ["control"], users: ["execution", "configuration"],
        scripts: ["execution"], queues: ["execution"], history: ["execution"],
      };
      let target = domains[domain];
      if (name.startsWith("src/ControlPlane/") || name.includes(".ControlPlane.")) target = ["control"];
      if (!target) { fullCore(`unregistered owner: ${reason}`); return; }
      for (const gate of target) host(`backend.${gate}`, reason);
      if (domain === "updates" || /src\/Host\/(?:StartupPipeline|HostInstance)/.test(name)) host("integration.restart-update", reason);
      if (domain === "plugins") host("integration.store", reason);
      return;
    }
    if (!/\.(md|png|jpg|svg|webp)$/i.test(name)) {
      fullCore(`unknown shared input: ${reason}`);
      return;
    }
    host("docs", reason);
  };
  const fullCore = reason => {
    for (const gate of registry.gates) {
      if (["scope", "aggregate", "release"].includes(gate.kind)) continue;
      if (gate.kind === "matrix-template") {
        for (const artifact of names) {
          const kind = policy.plugins[artifact].kind;
          const dimension = gate.id.split(".").at(-1);
          const manifestFile = path.join(root, policy.plugins[artifact].root, "plugin.json");
          const manifest = fs.existsSync(manifestFile) ? JSON.parse(fs.readFileSync(manifestFile,"utf8")) : null;
          if (kind === "data-specialized" ? ["contract","package","adapter"].includes(dimension)
            : (dimension !== "adapter" || artifact === "MaaFrameworkDriver") && (dimension !== "frontend" || manifest?.frontend))
            add(`${gate.id}:${artifact}`, reason);
        }
      } else add(gate.id,reason);
    }
  };
  if (!changes.length) fullCore("empty diff: conservative complete core");
  for (const change of changes) {
    for (const name of [change.old, change.path].filter(Boolean))
      classifyHost(normalize(name), change.status);
  }
  add(`${owner}.scope`, "always"); add(`${owner}.required`, "always");
  const declared = new Set(registry.gates.filter(gate => !gate.id.includes("${artifact}")).map(gate => gate.id));
  if (owner === "plugins") for (const gate of registry.gates.filter(gate => gate.kind === "matrix-template"))
    for (const artifact of names) declared.add(`${gate.id}:${artifact}`);
  for (const id of selected.keys()) if (!declared.has(id)) throw new Error(`Undeclared gate: ${id}`);
  return { selected: [...selected].sort().map(([id, reasons]) => ({ id, reasons: [...new Set(reasons)] })),
    notApplicable: [...declared].filter(id => !selected.has(id)).sort().map(id => ({ id, reason: "No affected input" })) };
}

export function createScopePlan(root, options = {}) {
  const { registry, digest } = readRegistry(root);
  const policy = JSON.parse(fs.readFileSync(path.join(root, "tests/policy.json"), "utf8"));
  const identity = collectChanges(root, options);
  const routing = planForChanges(root, identity.changes, registry, policy);
  const controlManifest=readControlManifest(root);
  const batchIdentity = {...identity, partnerSha:options.partnerSha ?? null, ...(options.inputPair ? {inputPair:options.inputPair} : {}), controlManifest};
  const partnerPolicy = options.partnerPolicy ?? (options.partnerRoot ? JSON.parse(fs.readFileSync(path.join(options.partnerRoot,"tests/policy.json"))) : null);
  const partnerPolicyDigest = partnerPolicy ? hash(JSON.stringify(partnerPolicy)) : null;
  const allocation = allocateUnits(coreUnits(routing.selected,registry,policy,partnerPolicy),{...batchIdentity,partnerPolicyDigest},policy.ciBatchPolicy);
  return { schemaVersion: 2, repository: registry.repository, policyDigest: hash(JSON.stringify(controlManifest)), digestFormat: "utf8-lf-v1",
    ...identity, partnerSha: options.partnerSha ?? null, runId: options.runId ?? null, attempt: options.attempt ?? null,
    inputMode: options.inputPair ? "paired" : "default", inputPair: options.inputPair ?? null, prNumber: options.prNumber ?? null, sourceFingerprint:sourceFingerprint(root), controlManifest, partnerPolicySnapshot:partnerPolicy, partnerPolicyDigest, ...routing, ...allocation };
}
