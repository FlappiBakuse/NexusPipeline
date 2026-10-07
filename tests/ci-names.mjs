import fs from "node:fs";
import path from "node:path";
import {pathToFileURL} from "node:url";

export const names = JSON.parse(fs.readFileSync(new URL("./ci-names.json", import.meta.url), "utf8"));

export function obligationNames(batch, registry) {
  const obligations = [...new Set(batch.units.flatMap(unit => unit.provides))].sort();
  return obligations.map(id => {
    const [template, artifact] = id.split(":");
    const gate = registry.gates.find(item => item.id === template);
    if (!gate) throw new Error(`Unregistered job obligation: ${id}`);
    return gate.name.replace("${artifact}", artifact ?? "");
  });
}

export function batchName(batch, registry) {
  if (!/^batch-(?:0[1-9]|[1-9][0-9]+)$/.test(batch.id) || !batch.units.length) throw new Error("Invalid named batch");
  const prefix = registry.repository;
  const labels = [...new Set(obligationNames(batch, registry).map(name => name.replace(`${prefix} / `, "")))];
  const title = labels.slice(0, 3).join("、") + (labels.length > 3 ? `等 ${labels.length} 项` : "");
  return `${prefix} / ${names.batch} ${batch.id.slice(6)} · ${title}`;
}

export function producerNames(plan, registry) {
  if (plan.repository !== registry.repository) throw new Error("Foreign job name registry");
  const prefix = plan.repository;
  return [
    `${prefix} / 范围判定`,
    ...(plan.control.units.length ? [`${prefix} / ${names.control}`] : []),
    ...plan.batches.map(batch => batchName(batch, registry)),
    `${prefix} / ${names.required}`
  ];
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  const [file] = process.argv.slice(2);
  if (!file || process.argv.length !== 3) throw new Error("Usage: ci-names.mjs <scope.json>");
  const registry = JSON.parse(fs.readFileSync(new URL("./gates.json", import.meta.url), "utf8"));
  console.log(JSON.stringify(producerNames(JSON.parse(fs.readFileSync(file, "utf8")), registry)));
}
