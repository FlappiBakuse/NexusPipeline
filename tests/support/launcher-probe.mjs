import { isMediumIntegrity } from "./windows-process.mjs";

const expectedEnvironmentValue = "expected-value";
const mediumIntegrity = isMediumIntegrity();
const environmentOk = process.env.NEXUS_LAUNCHER_PROBE === expectedEnvironmentValue;

console.log("[launcher-probe] stdout-ok");
console.error("[launcher-probe] stderr-ok");
console.log(`[launcher-probe] integrity=${mediumIntegrity ? "medium" : "not-medium"}`);
console.log(`[launcher-probe] ${environmentOk ? "env-ok" : "env-missing"}`);

if (!mediumIntegrity || !environmentOk) {
  process.exitCode = 1;
}
