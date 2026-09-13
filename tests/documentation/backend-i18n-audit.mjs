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

test("backend user-output adapters share the localized operation-error contract", () => {
  const english = JSON.parse(read("src/Localization/Resources/en-US.json"));
  const chinese = JSON.parse(read("src/Localization/Resources/zh-CN.json"));
  assert.deepEqual(Object.keys(english).sort(), Object.keys(chinese).sort(), "host locale resources must have identical keys");

  const operationResult = read("src/Application/Contracts/OperationResult.cs");
  const mcpResult = read("src/Mcp/McpToolResult.cs");
  const mcpHost = read("src/Mcp/McpHost.cs");
  const cliOutput = read("src/Cli/CliOutput.cs");
  assert.match(operationResult, /MessageKey/iu);
  assert.match(operationResult, /MessageArgs/iu);
  assert.match(mcpResult, /TranslateOperationError/iu);
  assert.doesNotMatch(mcpResult, /ErrorMessage\s*=\s*error\.Message/iu);
  assert.match(mcpHost, /LocaleContext\.Push/iu);
  assert.match(mcpHost, /X-Nexus-Locale/iu);
  assert.match(cliOutput, /TranslateUserMessage/iu);

  const sourceFiles = [
    "src/Mcp/McpMutationTools.cs",
    "src/Mcp/McpReadOnlyTools.cs",
    "src/Mcp/McpToolContext.cs",
    "src/Mcp/McpPolicy.cs",
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

  for (const relativePath of ["src/Application/Commands", "src/Mcp", "src/Cli"].flatMap(walk)) {
    const source = read(relativePath);
    for (const match of source.matchAll(/(?:OperationResult(?:<[^>]+>)?|McpToolResult)\.Failure\(\s*["']([a-z][a-z0-9_]*)["']/giu)) {
      assert.ok(Object.hasOwn(english, `api.error.${match[1]}`), `${relativePath} uses an unregistered user error code: ${match[1]}`);
    }
  }
});
