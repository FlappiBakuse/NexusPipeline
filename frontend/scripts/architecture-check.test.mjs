import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { checkFrontendArchitecture } from "./architecture-check.mjs";

test("A05 follows aliases and Vue scripts while allowing the public facade", () => {
  const root = fs.mkdtempSync(path.join(process.env.NEXUS_TEST_ARTIFACT_ROOT || os.tmpdir(), "frontend-arch-"));
  try {
    for (const directory of ["src/app", "src/features/demo", "src/plugin-bridge", "src/ui"])
      fs.mkdirSync(path.join(root, directory), { recursive: true });
    fs.writeFileSync(path.join(root, "tsconfig.json"), JSON.stringify({ compilerOptions: {
      baseUrl: ".", paths: { "@bridge/*": ["src/plugin-bridge/*"] }, moduleResolution: "Bundler",
    } }));
    fs.writeFileSync(path.join(root, "src/plugin-bridge/index.ts"), "export const safe = 1;\n");
    fs.writeFileSync(path.join(root, "src/plugin-bridge/runtime.ts"), "export const hidden = 1;\n");
    fs.writeFileSync(path.join(root, "src/app/Good.vue"), '<script setup lang="ts">\nimport { safe } from "@bridge/index";\n</script>\n');
    fs.writeFileSync(path.join(root, "src/ui/comment.ts"), '// import "@bridge/runtime"\nexport const safe = 1;\n');
    assert.equal(checkFrontendArchitecture(root).status, "PASS");
    fs.writeFileSync(path.join(root, "src/features/demo/Bad.vue"), '<script setup lang="ts">\nimport { hidden } from "@bridge/runtime";\n</script>\n');
    const report = checkFrontendArchitecture(root);
    assert.equal(report.status, "FAIL");
    assert.deepEqual(report.violations.map(item => item.ruleId), ["A05"]);
    assert.equal(report.violations[0].target, "src/plugin-bridge/runtime.ts");
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});
