import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");

function read(relativePath) {
  return fs.readFileSync(path.join(ROOT, relativePath), "utf8");
}

function walk(relativeDirectory) {
  const directory = path.join(ROOT, relativeDirectory);
  const files = [];
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    const relativePath = path.join(relativeDirectory, entry.name);
    if (entry.isDirectory()) files.push(...walk(relativePath));
    else if (entry.isFile() && entry.name.endsWith(".cs")) files.push(relativePath);
  }
  return files;
}

function withoutComments(source) {
  return source
    .replace(/\/\*[\s\S]*?\*\//gu, "")
    .replace(/\/\/[^\r\n]*/gu, "");
}

function assertNoChineseStringLiterals(source, label) {
  const code = withoutComments(source);
  for (const match of code.matchAll(/"(?:\\.|[^"\\])*"/gu)) {
    assert.doesNotMatch(match[0], /[\u3400-\u9fff]/u, `${label} contains a Chinese string literal: ${match[0]}`);
  }
}

function assertNoChineseOutputArguments(source, pattern, label) {
  for (const match of source.matchAll(pattern)) {
    assert.doesNotMatch(match[1], /[\u3400-\u9fff]/u, `${label} contains Chinese output text: ${match[1]}`);
  }
}

test("backend user-output adapters share the localized operation-error contract", () => {
  const english = JSON.parse(read("src/Shared/Localization/Resources/en-US.json"));
  const chinese = JSON.parse(read("src/Shared/Localization/Resources/zh-CN.json"));
  assert.deepEqual(Object.keys(english).sort(), Object.keys(chinese).sort(), "host locale resources must have identical keys");

  const operationResult = read("src/Shared/Results/OperationResult.cs");
  const mcpResult = read("src/ControlPlane/Mcp/McpToolResult.cs");
  const mcpHost = read("src/ControlPlane/Mcp/McpHost.cs");
  const cliOutput = read("src/ControlPlane/Cli/CliOutput.cs");
  assert.match(operationResult, /MessageKey/iu);
  assert.match(operationResult, /MessageArgs/iu);
  assert.match(mcpResult, /TranslateOperationError/iu);
  assert.doesNotMatch(mcpResult, /ErrorMessage\s*=\s*error\.Message/iu);
  assert.match(mcpHost, /LocaleContext\.Push/iu);
  assert.match(mcpHost, /X-Nexus-Locale/iu);
  assert.match(cliOutput, /TranslateUserMessage/iu);

  const sourceFiles = [
    "src/ControlPlane/Mcp/McpMutationTools.cs",
    "src/ControlPlane/Mcp/McpReadOnlyTools.cs",
    "src/ControlPlane/Mcp/McpToolContext.cs",
    "src/ControlPlane/Mcp/McpPolicy.cs",
  ];
  for (const relativePath of sourceFiles) {
    const source = read(relativePath);
    for (const match of source.matchAll(/McpToolResult\.Failure\(([\s\S]*?);/gu)) {
      const call = match[1];
      if (/[\u3400-\u9fff]/u.test(call)) {
        assert.match(call, /messageKey\s*:/u, `${relativePath} has an MCP failure with a source-language literal but no messageKey`);
      }
    }
  }

  const referencedKeys = new Set();
  for (const relativePath of sourceFiles) {
    const source = read(relativePath);
    for (const match of source.matchAll(/messageKey\s*:\s*["'](api\.[A-Za-z0-9_.-]+)["']/gu)) {
      referencedKeys.add(match[1]);
    }
  }
  for (const key of referencedKeys) {
    assert.ok(Object.hasOwn(english, key), `missing host localization key: ${key}`);
  }

  for (const relativePath of [
    "src/Modules/Scripts/UseCases",
    "src/Modules/Queues/UseCases",
    "src/Modules/Users/UseCases",
    "src/Modules/Settings/UseCases",
    "src/Modules/Configuration/Editing",
    "src/ControlPlane/Mcp",
    "src/ControlPlane/Cli",
  ].flatMap(walk)) {
    const source = read(relativePath);
    for (const match of source.matchAll(/(?:OperationResult(?:<[^>]+>)?|McpToolResult)\.Failure\(\s*["']([a-z][a-z0-9_]*)["']/giu)) {
      assert.ok(Object.hasOwn(english, `api.error.${match[1]}`), `${relativePath} uses an unregistered user error code: ${match[1]}`);
    }
  }

  for (const key of [
    "exit.active_runs",
    "exit.blocked_title",
    "exit.completion_delayed",
    "exit.config_edit_sessions",
    "exit.pending_system_action",
    "exit.request_rejected",
    "exit.shutdown_blocked",
    "exit.unknown_reason",
    "exit.waiting_for_safe_shutdown",
    "startup.admin_required",
    "startup.admin_title",
    "startup.limits_fatal",
  ]) {
    assert.ok(Object.hasOwn(english, key), `missing host lifecycle localization key: ${key}`);
  }

  const runtimeInitializer = read("src/Host/Initialization/RuntimeInitializer.cs");
  assertNoChineseStringLiterals(runtimeInitializer, "RuntimeInitializer");
  assertNoChineseOutputArguments(
    runtimeInitializer,
    /(?:MessageBox\.Show|Console\.Error\.WriteLine)\(([\s\S]*?)\);/gu,
    "RuntimeInitializer user output");

  const bootstrap = read("src/Host/Lifecycle/Bootstrap.cs");
  assert.match(bootstrap, /HostLocalization\.TranslateNamed/iu);
  const exitBoundaryStart = bootstrap.indexOf("internal static bool CanStopServices");
  const exitBoundaryEnd = bootstrap.indexOf("internal static bool TryRequestRestart");
  assert.ok(exitBoundaryStart >= 0 && exitBoundaryEnd > exitBoundaryStart, "Bootstrap exit boundary must remain auditable");
  assertNoChineseStringLiterals(bootstrap.slice(exitBoundaryStart, exitBoundaryEnd), "Bootstrap exit boundary");
  assertNoChineseOutputArguments(
    bootstrap,
    /MessageBox\.Show\(([\s\S]*?)\);/gu,
    "Bootstrap user output");

  const tray = read("src/Host/Tray/TrayApp.cs");
  assert.match(tray, /Text\s*=\s*HostLocalization\.TranslateNamed/iu);
  assert.doesNotMatch(tray, /Text\s*=\s*["'][^"'\r\n]*[\u3400-\u9fff]/u, "Tray icon text must use localized output");
  assert.doesNotMatch(
    tray,
    /(?:new ToolStripMenuItem|menu\.Items\.Add)\(\s*["'][^"'\r\n]*[\u3400-\u9fff]/u,
    "Tray menu labels must use localized output");
});
