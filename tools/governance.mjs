import fs from "node:fs";
import path from "node:path";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { collectChanges } from "../tests/scope-plan.mjs";
import { sourceFingerprint } from "../tests/control-inputs.mjs";
import { checkContracts, repositoryPath } from "./doc-contracts.mjs";

const git = (root, ...args) => execFileSync("git", ["-C", root, ...args], { encoding: "utf8", windowsHide: true, maxBuffer: 32 * 1024 * 1024 }).trim();
const aggregate = rules => ["FAIL", "NOT_CHECKED", "REVIEW"].find(status => rules.some(rule => rule.status === status)) || "PASS";

export function ownerOf(file) {
  const parts = file.split("/");
  if (parts[0] === "src") {
    if (parts[1] === "Modules" && parts.length > 3) return `Modules/${parts[2]}`;
    if (parts[1] === "NexusPipeline.Plugin.Abstractions") return "PluginSdk";
    if (["Host", "ControlPlane", "Platform", "Shared"].includes(parts[1])) return parts[1];
  }
  if (parts[0] === "frontend") {
    if (parts[1] === "src" && parts[2] === "features") return `Frontend/features/${parts[3]}`;
    if (parts[1] === "src" && ["app", "platform", "plugin-bridge", "ui"].includes(parts[2])) return `Frontend/${parts[2]}`;
    if (parts[1] === "public" && parts[2] === "i18n") return "Frontend/i18n";
    if (parts[1] === "scripts") return "Tools";
    return "Frontend";
  }
  if (parts.length === 1 && file.endsWith(".md") && file !== "AGENTS.md") return "Docs";
  return ({ desktop: "Desktop", tests: "Tests", docs: "Docs", tools: "Tools" })[parts[0]] || "Repository";
}

export function pathRule(root, change, baseSha) {
  const file = change.path, owner = ownerOf(file);
  const result = { rule: "GOV-FS-01", file, old: change.old, owner, status: "PASS" };
  if (file.startsWith("/") || /^[A-Za-z]:/.test(file) || file.split("/").includes(".."))
    return { ...result, status: "FAIL", reason: "Path escapes repository" };
  if (/^(config|data|history|logs|\.nxp|release|\.generated|\.artifacts)\//.test(file)
      || !file.startsWith("tests/fixtures/") && /(^|\/)(node_modules|bin|obj)\//.test(file))
    return { ...result, status: "FAIL", reason: "Runtime/build output is not source" };
  const jsonRole = !file.endsWith(".json") || /^(tests\/fixtures\/|frontend\/public\/i18n\/|tools\/schemas\/)/.test(file)
    || file === "docs/map.json" || /^(src|frontend|desktop)\/.*\/(?:i18n|schemas)\//.test(file);
  if (baseSha && (owner.startsWith("Modules/") || owner.startsWith("Frontend/features/"))) {
    const directory = owner.startsWith("Modules/") ? "src/" + owner : "frontend/src/features/" + owner.split("/").at(-1);
    if (!git(root, "ls-tree", "-d", "--name-only", baseSha, "--", directory))
      return { ...result, status: "REVIEW", reason: "New Owner directory absent from explicit baseline; confirm navigation and task scope" };
  }
  if (owner === "Repository" || !jsonRole || owner.startsWith("Modules/") && !fs.existsSync(path.join(root, "src", owner))
      || owner.startsWith("Frontend/features/") && !fs.existsSync(path.join(root, "frontend/src/features", owner.split("/").at(-1)))
      || owner === "Frontend/ui" && !/^frontend\/src\/ui\/(primitives|composites)\//.test(file))
    return { ...result, rule: jsonRole ? "GOV-FS-01" : "GOV-DIFF-01", status: "REVIEW", reason: "Confirm resource role/Owner using docs/map.json and adjacent source" };
  return result;
}

export function architectureDelta(current, baseline) {
  const invalid = report => !report || report.schemaVersion !== 1 || !["PASS", "FAIL"].includes(report.status)
    || !Array.isArray(report.edges) || !Array.isArray(report.violations) || !Array.isArray(report.modes)
    || !report.modes.every(mode => /^[a-f0-9]{64}$/.test(report.sourceFingerprints?.[mode] || ""))
    || report.edges.some(edge => !report.modes.includes(edge.Mode) || !edge.From || !edge.To);
  if (invalid(current)) return { rule: "GOV-ARCH-01", status: "NOT_CHECKED", reason: "Current semantic report with edges/modes required" };
  if (current.status === "FAIL" || current.violations.length) return { rule: "GOV-ARCH-01", status: "FAIL", violations: current.violations };
  if (invalid(baseline) || JSON.stringify([...current.modes].sort()) !== JSON.stringify([...baseline.modes].sort()))
    return { rule: "GOV-ARCH-01", status: "NOT_CHECKED", baselineUnavailable: true, currentEdges: current.edges };
  const keys = edges => new Set(edges.map(edge => `${edge.Mode}:${edge.From}->${edge.To}`));
  const before = keys(baseline.edges), after = keys(current.edges);
  const addedEdges = [...after].filter(edge => !before.has(edge)).sort(), removedEdges = [...before].filter(edge => !after.has(edge)).sort();
  return { rule: "GOV-ARCH-01", status: addedEdges.length ? "REVIEW" : "PASS", addedEdges, removedEdges,
    currentSourceFingerprints: current.sourceFingerprints, baselineSourceFingerprints: baseline.sourceFingerprints,
    qualification: "Supplied compiled semantic reports; caller must bind both reports to source identities" };
}

export function contextFor(root, { paths = [], keywords = [], owners = [], partnerRoot } = {}) {
  const facts = checkContracts(root);
  const apiFacts = { pluginApi: facts.find(fact => fact.file === "docs/reference/plugin-api/managed.md")?.expected,
    frontendApi: facts.find(fact => fact.file === "frontend/src/plugin-bridge/runtime.ts")?.expected };
  const navigation = JSON.parse(fs.readFileSync(repositoryPath(root, "", "docs/map.json"), "utf8"));
  const terms = [...keywords, ...owners].map(value => value.toLowerCase());
  const topics = navigation.topics.filter(topic => paths.some(file => (topic.codePaths || []).some(code => file === code || file.startsWith(code + "/") || code.startsWith(file + "/")))
    || terms.some(term => JSON.stringify(topic).toLowerCase().includes(term)));
  for (const topic of topics) repositoryPath(root, "docs", topic.path);
  const register = fs.readFileSync(repositoryPath(root, "", "frontend/src/ui/register.ts"), "utf8");
  const imports = new Map([...register.matchAll(/^import (\w+) from "(\.\/[^\"]+\.vue)";/gm)].map(match => [match[1], match[2]]));
  const body = register.match(/export const NEXUS_PUBLIC_ELEMENTS = \{([\s\S]*?)\} as const;/);
  if (!body) throw new Error("Cannot extract public UI registry");
  const publicElements = [...body[1].matchAll(/"(nxp-[\w-]+)": (\w+),/g)].map(match => {
    const source = imports.get(match[2]);
    if (!source) throw new Error(`Missing UI import: ${match[2]}`);
    const file = path.posix.normalize("frontend/src/ui/" + source);
    repositoryPath(root, "", file);
    return { element: match[1], component: match[2], file, layer: source.includes("/primitives/") ? "primitive" : "composite",
      reason: "Registered public Custom Element; assess its props/events before creating a new control" };
  }).sort((a, b) => a.element.localeCompare(b.element));
  const agents = fs.readFileSync(repositoryPath(root, "", "AGENTS.md"), "utf8");
  const sections = agents.split(/(?=^## )/m).filter(section => /^## [24]\./.test(section)
    || /^## 3\./.test(section) && paths.some(file => file.startsWith("src/"))
    || /^## 5\./.test(section) && (partnerRoot || paths.some(file => file.startsWith("frontend/")) || terms.some(term => /ui|frontend|plugin/.test(term))));
  return { apiFacts, agents: { file: "AGENTS.md", headings: agents.split(/\r?\n/).filter(line => /^#{1,3} /.test(line)), sections },
    topics, testDomains: [...new Set(topics.flatMap(topic => topic.testDomains || []))].sort(),
    publicElements, uiReference: "docs/reference/ui/README.md", uiReview: "Host imports internal Nxp components; plugins consume public nxp-* elements. Explain why candidates cannot be reused and choose primitive/composite/feature-local.",
    partner: partnerRoot ? { root: path.resolve(partnerRoot), lock: JSON.parse(fs.readFileSync(repositoryPath(partnerRoot, "", "host.lock.json"), "utf8")), head: git(partnerRoot, "rev-parse", "HEAD"), sourceFingerprint: sourceFingerprint(partnerRoot) }
      : { status: "NOT_CHECKED", reason: "No explicit Plugins path" } };
}

export function checkChanges(root, evidence, options = {}) {
  const rules = checkContracts(root), owners = new Set(), changes = [...new Map(evidence.changes.map(change => [JSON.stringify(change), change])).values()];
  for (const change of changes) {
    const owner = ownerOf(change.path); owners.add(owner);
    if (change.old) owners.add(ownerOf(change.old));
    if (["A", "R", "C"].includes(change.status)) rules.push(pathRule(root, change, evidence.baseSha));
    for (const file of [change.path, change.old].filter(Boolean))
      if (options.owners?.length && !options.owners.includes(ownerOf(file)))
        rules.push({ rule: "GOV-SCOPE-01", status: "REVIEW", file, owner: ownerOf(file), reason: "Owner outside declared task scope" });
    if (/^frontend\/src\/.*\.(ts|tsx|vue)$/.test(change.path) && change.status !== "D") {
      let previous = "";
      try { previous = git(root, "show", `${evidence.baseSha}:${change.old || change.path}`); } catch { /* Added files have no baseline blob. */ }
      const current = options.workingTree ? fs.readFileSync(repositoryPath(root, "", change.path), "utf8") : git(root, "show", `${evidence.headSha}:${change.path}`);
      if ((current.match(/@ts-nocheck/g) || []).length > (previous.match(/@ts-nocheck/g) || []).length)
        rules.push({ rule: "GOV-TYPE-01", status: "REVIEW", file: change.path, reason: "Added type diagnostic exemption" });
    }
    if (/^frontend\/src\/ui\/(primitives|composites)\/.*\.vue$/.test(change.path) && ["A", "C", "R"].includes(change.status))
      rules.push({ rule: "GOV-UI-01", status: "REVIEW", file: change.path, reason: options.uiReason || "Inspect registry candidates and record why they cannot be reused" });
    if (/^(src\/NexusPipeline\.csproj|src\/NexusPipeline.Plugin.Abstractions\/|version\.json|update-policy\.json|desktop\/package(?:-lock)?\.json)/.test(change.path))
      rules.push({ rule: "GOV-VER-01", status: "REVIEW", file: change.path, reason: "Check explicit version/contract authorization; tool cannot infer it" });
    if (options.governanceOnly && /^(src\/|frontend\/src\/|desktop\/|version\.json|update-policy\.json)/.test(change.path))
      rules.push({ rule: "GOV-VER-01", status: "FAIL", file: change.path, reason: "Explicit governance-only scope excludes product/contract/version source changes" });
  }
  if (options.architectureCurrent) rules.push(architectureDelta(options.architectureCurrent, options.architectureBaseline));
  const context = contextFor(root, { ...options, paths: changes.map(change => change.path) });
  if (options.partnerRoot) for (const [actual, expected] of [[context.partner.lock.hostApiVersion, context.apiFacts.pluginApi], [context.partner.lock.frontendApiVersion, context.apiFacts.frontendApi]])
    rules.push({ rule: "GOV-DOC-01", status: !expected ? "NOT_CHECKED" : actual === expected ? "PASS" : "FAIL", file: "host.lock.json", repository: "Plugins", actual, expected });
  return { schemaVersion: 1, repository: "Host", ...evidence, changes, workingTreeIncluded: Boolean(options.workingTree),
    sourceFingerprint: options.workingTree ? sourceFingerprint(root) : null,
    owners: [...owners].sort(), rules, status: aggregate(rules), context,
    notChecked: options.architectureCurrent ? [] : ["GOV-ARCH-01 semantic delta: no compiled current/baseline reports supplied; A01-A05 remain separate checks"] };
}

function main() {
  const args = process.argv.slice(2), operation = args.shift(), options = { owners: [], paths: [], keywords: [] };
  const names = { "--root": "root", "--base": "base", "--head": "head", "--partner-root": "partnerRoot", "--report": "report", "--ui-reason": "uiReason", "--architecture-current": "architectureCurrentPath", "--architecture-baseline": "architectureBaselinePath" };
  while (args.length) {
    const name = args.shift();
    if (name === "--working-tree" || name === "--governance-only") { options[name === "--working-tree" ? "workingTree" : "governanceOnly"] = true; continue; }
    const value = args.shift();
    if (!value || value.startsWith("--")) throw new Error(`Missing argument: ${name}`);
    if (["--owner", "--path", "--keyword"].includes(name)) options[{ "--owner": "owners", "--path": "paths", "--keyword": "keywords" }[name]].push(value);
    else if (names[name] && options[names[name]] === undefined) options[names[name]] = value;
    else throw new Error(`Unknown/duplicate argument: ${name}`);
  }
  const root = path.resolve(options.root || path.join(path.dirname(fileURLToPath(import.meta.url)), ".."));
  let report;
  if (operation === "preflight") report = { schemaVersion: 1, repository: "Host", headSha: git(root, "rev-parse", "HEAD"), workingTree: git(root, "status", "--short"), sourceFingerprint: sourceFingerprint(root), context: contextFor(root, options) };
  else if (operation === "check") {
    if (!/^[a-f0-9]{40}$/.test(options.base || "") || options.head && !/^[a-f0-9]{40}$/.test(options.head)) throw new Error("Supply complete base/head commit SHA");
    if (options.workingTree && options.head && git(root, "rev-parse", options.head) !== git(root, "rev-parse", "HEAD")) throw new Error("Working tree requires checked-out HEAD");
    for (const name of ["architectureCurrent", "architectureBaseline"]) if (options[name + "Path"]) options[name] = JSON.parse(fs.readFileSync(options[name + "Path"], "utf8"));
    const evidence = collectChanges(root, { base: options.base, head: options.head, includeWorkingTree: options.workingTree });
    if (evidence.dirty && !options.workingTree) throw new Error("Use --working-tree or a clean checkout; current file validators cannot represent an excluded dirty tree");
    if (evidence.headSha !== evidence.testedSha) throw new Error("Check requires the declared HEAD to be checked out");
    report = checkChanges(root, evidence, options);
  } else throw new Error("Usage: governance.mjs preflight|check [--base SHA --working-tree] [--owner Owner] [--path path] [--keyword keyword] [--partner-root path] [--report external.json]");
  const encoded = JSON.stringify(report, null, 2) + "\n";
  if (options.report) {
    const output = path.resolve(options.report), relative = path.relative(root, output);
    if (!relative.startsWith(".." + path.sep) && !path.isAbsolute(relative)) throw new Error("Report must be outside repository");
    fs.mkdirSync(path.dirname(output), { recursive: true }); fs.writeFileSync(output, encoded);
  } else process.stdout.write(encoded);
  if (report.status === "FAIL" || report.rules?.some(rule => rule.rule === "GOV-DOC-01" && rule.status === "NOT_CHECKED")) process.exitCode = 1;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try { main(); } catch (error) { console.error(error.message); process.exitCode = 1; }
}
