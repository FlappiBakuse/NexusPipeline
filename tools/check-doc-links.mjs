import path from "node:path";
import { fileURLToPath } from "node:url";
import { checkContracts } from "./doc-contracts.mjs";
import { checkDocuments } from "./markdown-docs.mjs";

const options = {};
const args = process.argv.slice(2);
for (let i = 0; i < args.length; i += 2) {
  if (!["--root", "--partner-root"].includes(args[i]) || !args[i + 1] || options[args[i]]) throw new Error("Usage: check-doc-links [--root repository] [--partner-root repository]");
  options[args[i]] = path.resolve(args[i + 1]);
}
const root = options["--root"] || path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const result = checkDocuments(root, { partnerRoot: options["--partner-root"] });
for (const rule of checkContracts(root)) {
  console.error(JSON.stringify(rule));
  if (["FAIL", "NOT_CHECKED", "REVIEW"].includes(rule.status)) result.failures.push(`${rule.rule} ${rule.file || ""}: ${rule.reason || "contract mismatch"}`);
}
for (const notice of result.notices) console.error(JSON.stringify({ status: "REVIEW", reason: notice }));
for (const failure of result.failures) console.error(failure);
console.error(JSON.stringify({ status: result.failures.length ? "FAIL" : "PASS", documents: result.documents, links: result.checked, crossRepositoryLinks: result.crossChecked, partner: options["--partner-root"] ? "CHECKED" : "NOT_CHECKED" }));
process.exitCode = result.failures.length ? 1 : 0;
