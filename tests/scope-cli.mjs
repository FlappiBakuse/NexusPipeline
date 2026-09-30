import fs from "node:fs";
import path from "node:path";
import { createScopePlan } from "./scope-plan.mjs";

export function runScopeCommand(root, args) {
  const allowed = new Set(["--base", "--head", "--partner", "--partner-root", "--output", "--include-working-tree"]);
  const values = {};
  for (let index = 0; index < args.length; index++) {
    const flag = args[index];
    if (!allowed.has(flag) || Object.hasOwn(values, flag)) throw new Error(`Unknown or duplicate scope option: ${flag}`);
    if (flag === "--include-working-tree") values[flag] = true;
    else {
      const value = args[++index];
      if (!value || value.startsWith("--")) throw new Error(`Missing value: ${flag}`);
      values[flag] = value;
    }
  }
  if (!/^[a-f0-9]{40}$/.test(values["--base"] ?? "")) throw new Error("--base must be a full commit SHA");
  for (const flag of ["--head", "--partner"])
    if (values[flag] && !/^[a-f0-9]{40}$/.test(values[flag])) throw new Error(`${flag} must be a full commit SHA`);
  const plan = createScopePlan(root, { base: values["--base"], head: values["--head"] ?? "HEAD",
    includeWorkingTree: Boolean(values["--include-working-tree"]), partnerSha: values["--partner"],
    partnerRoot: values["--partner-root"] ? path.resolve(values["--partner-root"]) : null,
    runId: process.env.GITHUB_RUN_ID ?? null, attempt: process.env.GITHUB_RUN_ATTEMPT ?? null });
  const result = JSON.stringify(plan, null, 2) + "\n";
  if (values["--output"]) {
    const target = path.resolve(values["--output"]);
    fs.mkdirSync(path.dirname(target), { recursive: true });
    fs.writeFileSync(target, result, "utf8");
  } else process.stdout.write(result);
  return plan;
}
