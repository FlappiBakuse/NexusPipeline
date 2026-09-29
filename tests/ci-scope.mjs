import fs from "node:fs";
import path from "node:path";
import { createScopePlan, readRegistry } from "./scope-plan.mjs";

const root = path.resolve(import.meta.dirname, "..");
const base = process.env.PR_BASE_SHA;
const head = process.env.PR_HEAD_SHA;
const output = process.env.SCOPE_RESULT;
const githubOutput = process.env.GITHUB_OUTPUT;
if (!/^[a-f0-9]{40}$/.test(base ?? "") || !/^[a-f0-9]{40}$/.test(head ?? "") || !output || !githubOutput)
  throw new Error("Complete PR identity and output paths are required");
const plan = createScopePlan(root, { base, head, runId: process.env.GITHUB_RUN_ID,
  attempt: process.env.GITHUB_RUN_ATTEMPT });
const { registry } = readRegistry(root);
const active = plan.selected.filter(item => !["host.scope", "host.required"].includes(item.id));
if (active.some(item => item.id === "host.partner-contract")) {
  const api = process.env.GITHUB_API_URL;
  const token = process.env.GITHUB_TOKEN;
  if (!api || !token) throw new Error("Partner resolution requires the read-only GitHub API");
  const reply = await fetch(`${api}/repos/FlappiBakuse/NexusPipeline-Plugins/git/ref/heads/main`, {
    headers: { Authorization: `Bearer ${token}`, Accept: "application/vnd.github+json" },
    signal: AbortSignal.timeout(8000),
  });
  if (!reply.ok) throw new Error(`Partner SHA lookup failed: ${reply.status}`);
  const body = await reply.json();
  if (!/^[a-f0-9]{40}$/.test(body?.object?.sha ?? "")) throw new Error("Partner ref is not a commit SHA");
  plan.partnerSha = body.object.sha;
}
const names = new Map(registry.gates.map(gate => [gate.id, gate.name]));
const matrix = { include: active.map(item => ({ id: item.id, key: item.id.replaceAll(".", "-"),
  name: names.get(item.id) })) };
if (matrix.include.some(item => !item.name)) throw new Error("Selected gate is not in the registry");
fs.mkdirSync(path.dirname(path.resolve(output)), { recursive: true });
fs.writeFileSync(output, JSON.stringify(plan, null, 2) + "\n");
fs.appendFileSync(githubOutput, `matrix=${JSON.stringify(matrix)}\ncount=${matrix.include.length}\npartnerSha=${plan.partnerSha ?? ""}\n`);
