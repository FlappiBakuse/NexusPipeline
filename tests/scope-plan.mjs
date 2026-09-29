import fs from "node:fs";
import path from "node:path";
import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";

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
    dirty: includeWorkingTree && changes.length > parseNameStatus(git(root, ["diff", "--name-status", "-z", "--find-renames", baseSha, headSha, "--"])).length,
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
    if (/^docs\/.*\.(md|png|jpg|svg|webp)$/i.test(name)) { host("docs", reason); return; }
    if (/^(README(?:\.en)?\.md|LICENSE(?:\.md)?|NOTICE(?:\.md)?)$/i.test(name)) {
      host("docs", reason); host("release-contract", reason); return;
    }
    if (/^(tests\/(?:scope-plan|scope-cli|selection-cases|ci-scope|gate-required|test_gate_required|policy|gates|run|ci-gate|audit-jobs|test_audit_jobs)|\.github\/workflows\/ci\.yml)/.test(name)) {
      host("ci-policy", reason); return;
    }
    if (/^(tools\/installer|tools\/host_installer|tools\/installer-languages)/.test(name)) {
      host("installer-localization", reason); host("release-contract", reason); return;
    }
    if (/^(tools\/host_release|tools\/host_candidate|\.github\/workflows\/release)/.test(name)) {
      host("release-contract", reason); return;
    }
    if (name.startsWith("tools/NexusPipeline.Architecture/") || name === "tests/architecture-fixture.py") {
      host("architecture.backend", reason); return;
    }
    if (name === "tests/partner-contract.py") { host("partner-contract", reason); return; }
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
      if (!target) { target = ["execution", "configuration", "settings-notifications", "plugins", "updates-restart", "control", "scheduling"]; host("ci-policy", `unregistered owner: ${reason}`); }
      for (const gate of target) host(`backend.${gate}`, reason);
      if (domain === "updates" || /src\/Host\/(?:StartupPipeline|HostInstance)/.test(name)) host("integration.restart-update", reason);
      if (domain === "plugins") host("integration.store", reason);
      return;
    }
    if (/\.(csproj|props|targets|sln|json|yml|yaml|mjs|js|ts|vue|py)$/i.test(name)) {
      host("ci-policy", `unknown input: ${reason}`);
      for (const gate of ["backend.execution", "backend.configuration", "backend.settings-notifications", "backend.plugins", "backend.updates-restart", "backend.control", "backend.scheduling", "frontend.typecheck", "frontend.state", "frontend.build", "architecture.backend", "architecture.frontend"])
        host(gate, `unknown input: ${reason}`);
      return;
    }
    host("docs", reason);
  };
  const classifyPlugins = (name, status) => {
    const reason = `${status} ${name}`;
    if (/^(catalog\.json|\.release-state\.json|packages\/)/.test(name)) { add("plugins.release-contract", reason); return; }
    if (/^(tools\/repository|tools\/.*release|\.github\/workflows\/(?:stable|develop|release))/.test(name)) {
      add("plugins.release-contract", reason); add("plugins.ci-policy", reason); return;
    }
    if (/^(tests\/(?:scope-plan|scope-cli|selection|ci-scope|gate-runner|gate-required|policy|gates|run|ci-gate|audit-jobs|test_audit_jobs|inputs\.lock)|\.github\/workflows\/ci\.yml)/.test(name)) {
      add("plugins.ci-policy", reason); if (name === "tests/inputs.lock.json") add("plugins.inventory", reason); return;
    }
    if (name.startsWith("tests/architecture-check") || name === "tests/test_architecture_check.py") {
      add("plugins.inventory", reason); return;
    }
    if (name === "host.lock.json") {
      add("plugins.inventory", reason);
      for (const artifact of names) plugin("contract", artifact, reason);
      return;
    }
    if (/^(docs\/.*\.(md|png|jpg|svg|webp)|README\.md|CONTRIBUTING\.md|CHANGELOG\.md)$/i.test(name)) {
      add("plugins.docs", reason); return;
    }
    const artifact = names.find(item => name === policy.plugins[item].root || name.startsWith(policy.plugins[item].root + "/"));
    if (artifact) {
      const rootName = policy.plugins[artifact].root;
      const relative = name.slice(rootName.length + 1);
      const kind = policy.plugins[artifact].kind;
      const manifestPath = path.join(root, rootName, "plugin.json");
      const manifest = fs.existsSync(manifestPath) ? JSON.parse(fs.readFileSync(manifestPath, "utf8")) : null;
      if (relative === "plugin.json" || status === "D" && name === rootName) {
        add("plugins.inventory", reason); add("plugins.release-contract", reason);
        for (const dimension of ["contract", "package", "adapter", "component", "frontend", "capability"])
          if (kind === "data-specialized" ? !["component", "frontend", "capability"].includes(dimension)
            : dimension !== "adapter" || artifact === "MaaFrameworkDriver")
            if (dimension !== "frontend" || manifest?.frontend) plugin(dimension, artifact, reason);
      } else if (/\.md$/i.test(relative)) {
        add("plugins.docs", reason); plugin("package", artifact, reason);
      } else if (relative === "store.json" || relative.startsWith("changelog")) {
        plugin("contract", artifact, reason); plugin("package", artifact, reason);
      } else if (kind === "data-specialized") {
        plugin("contract", artifact, reason); plugin("adapter", artifact, reason);
      } else if (relative.startsWith("frontend/")) {
        plugin("frontend", artifact, reason); plugin("capability", artifact, reason);
      } else {
        plugin("contract", artifact, reason); plugin("component", artifact, reason); plugin("capability", artifact, reason);
      }
      return;
    }
    if (/\.(cs|ts|js|json|yml|yaml|mjs|vue|csproj|py)$/i.test(name)) {
      add("plugins.ci-policy", `unknown input: ${reason}`);
      for (const item of names) {
        plugin("contract", item, `unknown input: ${reason}`);
        plugin(policy.plugins[item].kind === "data-specialized" ? "adapter" : "component", item, `unknown input: ${reason}`);
      }
      return;
    }
    add("plugins.docs", reason);
  };
  for (const change of changes) {
    for (const name of [change.old, change.path].filter(Boolean))
      (owner === "host" ? classifyHost : classifyPlugins)(normalize(name), change.status);
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
  return { schemaVersion: 1, repository: registry.repository, policyDigest: digest, digestFormat: "utf8-lf-v1",
    ...identity, partnerSha: options.partnerSha ?? null, runId: options.runId ?? null, attempt: options.attempt ?? null,
    ...routing };
}
