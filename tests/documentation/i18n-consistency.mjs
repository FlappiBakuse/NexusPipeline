import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const RESOURCE_FILES = ["zh-CN.json", "en-US.json"];
const SAFE_KEY = /^[A-Za-z][A-Za-z0-9_.-]*$/u;
const PLACEHOLDER = /\{([A-Za-z][A-Za-z0-9_.-]*)\}/gu;
const CJK = /[\u4e00-\u9fff]/u;
const FORBIDDEN_KEY_PATTERNS = [
  { pattern: /(?:^|[._])(?:for_example|enter_the|failed_to|complete_the)(?:[._]|$)/u, description: "sentence-derived key" },
  { pattern: /_s_/u, description: "ambiguous plural suffix" },
  { pattern: /(?:_value){2,}/u, description: "duplicated value suffix" },
  { pattern: /[._][a-f0-9]{8}$/u, description: "hash-derived suffix" },
];
const SOURCE_EXTENSIONS = new Set([".js", ".mjs", ".html"]);

function read(relativePath) {
  return fs.readFileSync(path.join(ROOT, relativePath), "utf8");
}

function walkSources(directory) {
  const files = [];
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    if (entry.name === "node_modules" || entry.name === ".artifacts") continue;
    const absolute = path.join(directory, entry.name);
    if (entry.isDirectory()) files.push(...walkSources(absolute));
    else if (entry.isFile() && SOURCE_EXTENSIONS.has(path.extname(entry.name).toLowerCase())) files.push(absolute);
  }
  return files;
}

function resourceData() {
  return RESOURCE_FILES.map(file => ({
    file,
    values: JSON.parse(read(`wwwroot/i18n/${file}`)),
  }));
}

function placeholders(value) {
  return new Set([...String(value).matchAll(PLACEHOLDER)].map(match => match[1]));
}

function lineNumber(text, index) {
  return text.slice(0, index).split(/\r?\n/u).length;
}

function withoutComments(text) {
  return text
    .replace(/\/\*[\s\S]*?\*\//gu, "")
    .replace(/\/\/[^\r\n]*/gu, "");
}

function collectSourceKeyReferences(text, relativePath) {
  const references = [];
  const direct = /\bt\(\s*["']([A-Za-z][A-Za-z0-9_.-]*)["']/gu;
  for (const match of text.matchAll(direct)) {
    references.push({ key: match[1], line: lineNumber(text, match.index ?? 0), relativePath });
  }
  const attributes = /data-i18n(?:-title|-aria-label|-placeholder)?=["']([^"']+)["']/gu;
  for (const match of text.matchAll(attributes)) {
    references.push({ key: match[1], line: lineNumber(text, match.index ?? 0), relativePath });
  }
  const dynamic = /\bt\(\s*`([A-Za-z][A-Za-z0-9_.-]*\.)\$\{/gu;
  for (const match of text.matchAll(dynamic)) {
    references.push({ prefix: match[1], line: lineNumber(text, match.index ?? 0), relativePath });
  }
  return references;
}

test("host locale resources use the same safe key and placeholder contract", () => {
  const [defaultResource, englishResource] = resourceData();
  const defaultKeys = Object.keys(defaultResource.values);
  const englishKeys = Object.keys(englishResource.values);
  assert.deepEqual(englishKeys.sort(), defaultKeys.slice().sort(), "locale key sets must match");
  assert.deepEqual(defaultKeys, defaultKeys.slice().sort(), "locale keys must be sorted");
  for (const [index, key] of defaultKeys.entries()) {
    assert.match(key, SAFE_KEY, `unsafe locale key at index ${index}: ${key}`);
    assert.ok(key.length <= 40, `locale key is too long at index ${index}: ${key}`);
    assert.doesNotMatch(key, /^(?:ui|legacy)\./u, `legacy host locale namespace: ${key}`);
    for (const { pattern, description } of FORBIDDEN_KEY_PATTERNS) {
      assert.doesNotMatch(key, pattern, `${description} is not allowed: ${key}`);
    }
    assert.equal(typeof defaultResource.values[key], "string", `default locale value must be a string: ${key}`);
    assert.equal(typeof englishResource.values[key], "string", `English locale value must be a string: ${key}`);
    assert.ok(defaultResource.values[key].trim(), `default locale value is empty: ${key}`);
    assert.ok(englishResource.values[key].trim(), `English locale value is empty: ${key}`);
    assert.doesNotMatch(englishResource.values[key], /\(\s*s\s*\)/u, `ambiguous English plural marker: ${key}`);
    assert.deepEqual(
      [...placeholders(defaultResource.values[key])].sort(),
      [...placeholders(englishResource.values[key])].sort(),
      `placeholder sets must match: ${key}`,
    );
  }
});

test("frontend translation references resolve to current host locale resources", () => {
  const keys = new Set(Object.keys(JSON.parse(read("wwwroot/i18n/zh-CN.json"))));
  const missing = [];
  const dynamicPrefixes = [];
  for (const absolute of walkSources(path.join(ROOT, "wwwroot"))) {
    const relative = path.relative(ROOT, absolute).replaceAll(path.sep, "/");
    const text = fs.readFileSync(absolute, "utf8");
    for (const reference of collectSourceKeyReferences(text, relative)) {
      if (reference.prefix) {
        if (![...keys].some(key => key.startsWith(reference.prefix))) dynamicPrefixes.push(`${relative}:${reference.line} -> ${reference.prefix}`);
      } else if (!keys.has(reference.key)) {
        missing.push(`${relative}:${reference.line} -> ${reference.key}`);
      }
    }
  }
  assert.deepEqual(missing, [], `missing frontend locale keys:\n${missing.join("\n")}`);
  assert.deepEqual(dynamicPrefixes, [], `missing dynamic frontend locale prefixes:\n${dynamicPrefixes.join("\n")}`);
});

test("frontend sources have no legacy ui namespace or hardcoded localized list punctuation", () => {
  const failures = [];
  for (const absolute of walkSources(path.join(ROOT, "wwwroot"))) {
    const relative = path.relative(ROOT, absolute).replaceAll(path.sep, "/");
    const text = fs.readFileSync(absolute, "utf8");
    if (/\bui\.(?!js\b)/u.test(text)) failures.push(`${relative}: legacy ui namespace`);
    if (/\.join\(\s*["'](?:、|；|，|：)["']\s*\)/u.test(text)) failures.push(`${relative}: hardcoded localized list punctuation`);
    if (/^(?:const|let)\s+\w+\s*=\s*t\(/mu.test(text)) failures.push(`${relative}: module-level translation cache`);
    if (CJK.test(withoutComments(text))) failures.push(`${relative}: hardcoded CJK outside comments`);
  }
  assert.deepEqual(failures, [], `i18n source contract failures:\n${failures.join("\n")}`);
});
