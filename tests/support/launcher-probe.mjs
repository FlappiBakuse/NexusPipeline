import { spawnSync } from "node:child_process";
import { isMediumIntegrity } from "./windows-process.mjs";

const expectedEnvironmentValue = "expected-value";
const mediumIntegrity = isMediumIntegrity();
const environmentOk = process.env.NEXUS_LAUNCHER_PROBE === expectedEnvironmentValue;

console.log("[launcher-probe] stdout-ok");
console.error("[launcher-probe] stderr-ok");
console.log(`[launcher-probe] integrity=${mediumIntegrity ? "medium" : "not-medium"}`);
console.log(`[launcher-probe] ${environmentOk ? "env-ok" : "env-missing"}`);

if (!mediumIntegrity) {
  const result = spawnSync("whoami", ["/groups"], { encoding: "utf8", windowsHide: true });
  const output = `${result.stdout || ""}\n${result.stderr || ""}`;
  const integritySids = output.match(/S-1-16-\d+/gi) || [];
  console.error(`[launcher-probe] whoami-status=${result.status ?? "null"}`);
  console.error(`[launcher-probe] whoami-error=${result.error?.message || "none"}`);
  console.error(`[launcher-probe] whoami-integrity-sids=${integritySids.join(",") || "none"}`);
}

if (!mediumIntegrity || !environmentOk) {
  process.exitCode = 1;
}
