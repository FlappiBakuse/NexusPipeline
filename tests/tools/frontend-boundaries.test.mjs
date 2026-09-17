import test from "node:test";
import assert from "node:assert/strict";
import { scanFrontendBoundaries, scanSource } from "../../tools/frontend-boundaries.mjs";

test("frontend boundary scanner keeps the reviewed Vue adapter as the only private-field owner", () => {
  const allowed = scanSource("frontend/src/ui/register.ts", "const value = element._instance; element._mount(element._def); element._app?.unmount();");
  assert.equal(allowed.filter(finding => finding.severity === "error").length, 0);
  assert.equal(allowed.filter(finding => finding.ruleId === "private-vue-adapter").length, 3);

  const rejected = scanSource("frontend/src/features/example/Example.vue", "<script setup>const value = element._instance;</script><template><div /></template>");
  assert.equal(rejected.some(finding => finding.ruleId === "private-vue-field-leak"), true);
});

test("bridge boundary rejects native controls and accepts public elements", () => {
  const rejected = scanSource("frontend/src/plugin-bridge/slots.ts", "const save = document.createElement(\"button\");");
  assert.equal(rejected.some(finding => finding.ruleId === "native-dynamic-interactive-bypass"), true);

  const accepted = scanSource("frontend/src/plugin-bridge/slots.ts", "const save = document.createElement(\"nxp-button\");");
  assert.equal(accepted.some(finding => finding.kind === "dynamic-interactive-element"), false);
});

test("plugin sources cannot import host private UI SFCs", () => {
  const rejected = scanSource(
    "plugins/general/Example/frontend/src/App.vue",
    "import NxpButton from '../../../../frontend/src/ui/primitives/NxpButton.vue';",
    { plugin: true },
  );
  assert.equal(rejected.some(finding => finding.ruleId === "plugin-private-host-import"), true);

  const accepted = scanSource(
    "plugins/general/Example/frontend/src/App.vue",
    "const button = document.createElement('nxp-button');",
    { plugin: true },
  );
  assert.equal(accepted.some(finding => finding.severity === "error"), false);
});

test("hidden payload and precise semantic exceptions are visible in the report", () => {
  const report = scanFrontendBoundaries({
    sources: {
      "frontend/src/features/history/components/RangePicker.vue": '<template><input type="hidden" :value="from" /></template>',
      "frontend/src/app/App.vue": '<template><button aria-label="close" data-i18n-aria-label="shell.close_navigation" /></template>',
      "frontend/src/features/dashboard/Example.vue": "<template><div>content</div></template>",
    },
  });
  assert.equal(report.ok, true);
  assert.deepEqual(
    report.findings.filter(finding => finding.severity === "info").map(finding => finding.ruleId),
    ["hidden-native-input", "navigation-backdrop"],
  );
});

test("ordinary feature markup is scanned without a directory-wide allowlist", () => {
  const report = scanFrontendBoundaries({
    sources: {
      "frontend/src/features/example/Example.vue": "<template><button class=\"unregistered\">Save</button></template>",
    },
  });
  assert.equal(report.ok, false);
  assert.equal(report.issues[0].ruleId, "native-interactive-bypass");
});

test("nested templates and parameterized dynamic controls remain visible while comments are ignored", () => {
  const source = '<script setup>/* document.createElement("button") */ const input = h("input", { type: "text" });</script><template><div><template v-if="ok"><span /></template><button :disabled="count > 0">Save</button></div></template>';
  const findings = scanSource("frontend/src/features/example/Example.vue", source);
  assert.equal(findings.filter(item => item.ruleId === "native-interactive-bypass").length, 1);
  assert.equal(findings.filter(item => item.ruleId === "native-dynamic-interactive-bypass").length, 1);
});
