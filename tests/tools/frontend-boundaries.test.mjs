import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import test from "node:test";
import assert from "node:assert/strict";
import { fileURLToPath } from "node:url";
import { readProductionSources, scanFrontendBoundaries, scanSource } from "../../tools/frontend-boundaries.mjs";

const toolPath = fileURLToPath(new URL("../../tools/frontend-boundaries.mjs", import.meta.url));

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

test("production source scanning covers CSS, JavaScript, MJS, and HTML", () => {
  const findings = [
    ...scanSource("frontend/src/features/example/styles.css", ".feature-shell .nxp-button { color: red; }"),
    ...scanSource("frontend/src/features/example/widget.js", "const element = document.createElement(\"button\");"),
    ...scanSource("frontend/src/features/example/widget.mjs", "const element = document.createElement(\"button\");"),
    ...scanSource("frontend/src/features/example/widget.html", "<main><section><button>Save</button></section></main>"),
  ];

  assert.equal(findings.some(item => item.ruleId === "private-style-selector"), true);
  assert.equal(findings.filter(item => item.ruleId === "native-dynamic-interactive-bypass").length, 2);
  assert.equal(findings.filter(item => item.ruleId === "native-interactive-bypass").length, 1);
});

test("CSS 私有样式边界覆盖私有类、公开控件后代与分组选择器", () => {
  const findings = scanSource(
    "frontend/src/features/example/styles.css",
    ".nxp-scroll-viewport { overflow: auto; }\n"
      + "nxp-modal .nxp-scroll-viewport { overflow: auto; }\n"
      + ":is(.feature-shell, .nxp-scroll-viewport) { color: red; }\n"
      + "nxp-modal { display: block; }",
  );
  const privateStyles = findings.filter(item => item.ruleId === "private-style-selector");
  assert.equal(privateStyles.length, 3);
});

test("component ownership catches migrated generic implementation classes and allows public roots", () => {
  const rejectedSelectors = [
    ".switch-card { display: flex; }",
    ".settings-card-toggle { display: flex; }",
    ".plugin-loading-state { display: grid; }",
    ".badge { display: inline-flex; }",
    ".empty strong { display: block; }",
  ];
  for (const selector of rejectedSelectors) {
    const findings = scanSource("frontend/src/features/example/styles.css", selector);
    assert.equal(findings.some(item => item.ruleId === "private-style-selector"), true, selector);
  }

  const accepted = scanSource("frontend/src/features/example/styles.css", "nxp-modal { margin: 12px; }");
  assert.equal(accepted.some(item => item.ruleId === "private-style-selector"), false);

  const internal = scanSource(
    "frontend/src/ui/primitives/NxpBadge.vue",
    "<template><span class=\"nxp-badge\" /></template><style>.nxp-badge { display: inline-flex; }</style>",
  );
  assert.equal(internal.some(item => item.ruleId === "private-style-selector"), false);
});

test("bridge static assembly cannot recreate migrated public component classes", () => {
  const rejected = scanSource(
    "frontend/src/plugin-bridge/slots.ts",
    "const badge = document.createElement('span'); badge.className = 'badge ok';",
  );
  assert.equal(rejected.some(item => item.ruleId === "private-component-class-usage" && item.token === "badge"), true);

  const accepted = scanSource(
    "frontend/src/plugin-bridge/slots.ts",
    "const badge = document.createElement('nxp-badge'); badge.tone = 'ok';",
  );
  assert.equal(accepted.some(item => item.ruleId === "private-component-class-usage"), false);
});

test("frontend boundary CLI scans the real production file tree", () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "nxp-frontend-boundaries-"));
  try {
    const sourceRoot = path.join(root, "frontend", "src", "features", "example");
    fs.mkdirSync(sourceRoot, { recursive: true });
    fs.writeFileSync(path.join(sourceRoot, "styles.css"), ".feature-shell .nxp-button { color: red; }\n");
    fs.writeFileSync(path.join(sourceRoot, "widget.js"), "const element = document.createElement(\"button\");\n");
    fs.writeFileSync(path.join(sourceRoot, "widget.mjs"), "const element = document.createElement(\"button\");\n");
    fs.writeFileSync(path.join(sourceRoot, "widget.html"), "<main><button>Save</button></main>\n");
    fs.writeFileSync(path.join(root, "frontend", "index.html"), "<main>app</main>\n");
    fs.writeFileSync(path.join(sourceRoot, "ignored.test.ts"), "const element = document.createElement(\"button\");\n");

    const entries = readProductionSources(root);
    assert.equal(entries.length, 5);
    const result = spawnSync(process.execPath, [toolPath, "--root", root, "--quiet"], { encoding: "utf8" });
    assert.equal(result.status, 1, result.stderr);
    assert.match(result.stdout, /扫描 5 个生产文件/u);
    assert.match(result.stdout, /private-style-selector/u);
    assert.match(result.stdout, /widget\.mjs.*native-dynamic-interactive-bypass/su);
    assert.match(result.stdout, /widget\.html.*native-interactive-bypass/su);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});
