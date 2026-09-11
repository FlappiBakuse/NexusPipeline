import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const SOURCE_EXTENSIONS = new Set([".js", ".mjs", ".html", ".ts", ".vue"]);

// 这些前缀对应运行时从 API 错误码、诊断结果或状态码动态选择的模板。
// 新增动态资源时应先把调用契约写清楚，再把最小前缀加入这里。
const REVIEWED_UNUSED_PREFIXES = new Map([
  ["api.", "API 错误码运行时选择"],
  ["auth.", "认证响应运行时选择"],
  ["diagnostics.", "诊断类别、检查项和结果状态运行时选择"],
  ["dispatch.reason.", "调度解释码运行时选择"],
  ["dispatch.warning.", "调度警告码运行时选择"],
  ["settings.update_idle.", "更新闲时状态码运行时选择"],
]);

// 已完成首次 inventory 并确认仍由运行时之外的宿主流程保留的词条。
// 该集合只允许当前审计基线中的孤立资源；新增孤立 key 会直接失败。
const REVIEWED_UNUSED_KEYS = new Set([
  "common.error",
  "common.mode_toggle.off",
  "common.mode_toggle.on",
  "common.access_token_required",
  "common.action.add_schedule",
  "common.action.add_task",
  "common.action.decrease",
  "common.action.increase",
  "common.action.open_management",
  "common.all_script_instances_bound",
  "common.attention",
  "common.binding.select_help",
  "common.binding_configuration",
  "common.binding_configuration_copy",
  "common.catalog.cached",
  "common.check_this_field",
  "common.checks_need_attention",
  "common.close_notification",
  "common.configuration.multiple_help",
  "common.configuration.takeover_select",
  "common.configuration_restored",
  "common.confirm.remove_user",
  "common.confirm.remove_user_help",
  "common.confirm.scheduled_overlap",
  "common.dependencies",
  "common.details",
  "common.details.recommendations",
  "common.diagnostic_check",
  "common.enter_token",
  "common.error.host_version_incompatible",
  "common.error.script_instance_missing",
  "common.features.controls_help",
  "common.fetching_catalog",
  "common.first_edit_copy",
  "common.host_language",
  "common.hour",
  "common.images",
  "common.interface_language",
  "common.language.browser_only",
  "common.language.label",
  "common.limit.enabled_user_count",
  "common.limit.schedule_count",
  "common.log.tail_summary",
  "common.logs",
  "common.minute",
  "common.modal_cleanup_failed",
  "common.new",
  "common.no_diagnostic_results",
  "common.no_plugins_need_updates",
  "common.normal",
  "common.notification.screenshots_disabled",
  "common.open_time_picker",
  "common.overall_status",
  "common.plugin_runtime_state_is_unhealthy",
  "common.queue.remove_task_before_save",
  "common.readme.unavailable",
  "common.recommendation",
  "common.recovery",
  "common.retry.attempts_label",
  "common.schedule",
  "common.script_instance.none_available",
  "common.select_time",
  "common.simplified_chinese",
  "common.status.all_passed_or_skipped",
  "common.system_settings",
  "common.unit.items",
  "common.unrecognized_specialized_script",
  "common.update.pre_release_available",
  "common.update.started",
  "common.update_all_plugins",
  "common.user.name_case_help",
  "common.user.name_placeholder",
  "common.validation.specialized_response",
  "settings.checks",
  "settings.host",
  "settings.language_en",
  "settings.language_zh",
  "settings.network",
  "settings.other",
  "settings.service",
  "settings.service_restart",
  "settings.settings",
  "shell.close_nav",
  "shell.open_nav",
  "users.choose_script_instances",
  "users.configuration_check",
  "users.no_script_instances_bound",
]);

const REVIEWED_AMBIGUOUS_KEYS = new Set([
  "common.binding_configuration_copy",
  "common.first_edit_copy",
  "common.limits_warning_copy",
  "common.theme_value",
  "queues.edit_queue_button",
  "settings.there_is_no_token_to_copy",
  "users.deleted_user_value",
  "users.user_management_button",
]);

// 只有确实承担片段职责的资源才允许使用 suffix/prefix 命名。
const REVIEWED_FRAGMENT_KEYS = new Set(["shell.brand_suffix"]);
const REVIEWED_ROLE_EXCEPTIONS = new Set(["settings.subject_prefix"]);
const AMBIGUOUS_SUFFIXES = ["_copy", "_button", "_value", "_text"];
// 品牌名与 <script setup> 类型声明不构成用户可见英文硬编码。
const ENGLISH_LITERAL_ALLOWLIST = new Set([
  "README",
  "NexusPipeline",
  "N",
  "Nexus UI",
  "defineProps",
  "defineEmits",
  "withDefaults(defineProps",
]);
const TYPESCRIPT_DECLARATION_LINE = /^\s*(?:withDefaults\(|defineProps|defineEmits|const \w+ = withDefaults)/u;

function read(relativePath) {
  return fs.readFileSync(path.join(ROOT, relativePath), "utf8");
}

function readLocaleIds() {
  const registry = JSON.parse(read("frontend/public/i18n/locales.json"));
  const locales = Array.isArray(registry?.supported)
    ? registry.supported.map(item => typeof item === "string" ? item : item?.id).filter(Boolean)
    : [];
  assert.ok(locales.length > 0, "Web locale registry 必须声明至少一个 locale");
  return locales;
}

function readResources(locales) {
  return Object.fromEntries(locales.map(locale => [
    locale,
    JSON.parse(read(`frontend/public/i18n/${locale}.json`)),
  ]));
}

function walkSources(directory) {
  const files = [];
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    if (entry.name === "node_modules" || entry.name === ".artifacts") continue;
    const absolute = path.join(directory, entry.name);
    if (entry.isDirectory()) files.push(...walkSources(absolute));
    else if (entry.isFile() && SOURCE_EXTENSIONS.has(path.extname(entry.name).toLowerCase()) && !/\.test\.[cm]?[jt]s$/u.test(entry.name)) files.push(absolute);
  }
  return files;
}

function stripComments(source) {
  return source
    .replace(/<!--[\s\S]*?-->/gu, match => match.replace(/[^\r\n]/gu, " "))
    .replace(/\/\*[\s\S]*?\*\//gu, match => match.replace(/[^\r\n]/gu, " "))
    .replace(/(^|[\r\n])[ \t]*\/\/[^\r\n]*/gu, "$1");
}

function lineNumber(source, index) {
  return source.slice(0, index).split("\n").length;
}

function relativePath(absolute) {
  return path.relative(ROOT, absolute).replaceAll(path.sep, "/");
}

function usageRoles(source, index) {
  const context = source.slice(Math.max(0, index - 600), Math.min(source.length, index + 600));
  const roles = new Set();
  if (/data-i18n-placeholder|\bplaceholder\s*[:=]/iu.test(context)) roles.add("placeholder");
  if (/data-i18n-title|\btitle\s*=/iu.test(context)) roles.add("title");
  if (/data-i18n-aria-label|\baria-label\s*=/iu.test(context)) roles.add("aria-label");
  if (/class\s*=\s*["'][^"']*\bbadge\b|\bNxpBadge\b/iu.test(context)) roles.add("badge");
  if (/data-help|\bhelp\b|\bdescription\b/iu.test(context)) roles.add("help");
  if (/\btoast\s*\(/u.test(context)) roles.add("toast");
  if (/\b(?:button|action|data-action)\b/iu.test(context)) roles.add("action");
  if (/\b(?:status|state|summary|count)\b/iu.test(context)) roles.add("status");
  if (/\bt\s*\(/u.test(context)) roles.add("translation");
  if (roles.size === 0) roles.add("literal");
  return [...roles].sort();
}

function collectReferenceGraph(resources) {
  const knownKeys = new Set(Object.keys(resources[Object.keys(resources)[0]]));
  const references = [];
  const dynamicPrefixes = new Set();
  const seen = new Set();

  function addReference(key, absolute, source, index, kind) {
    if (!knownKeys.has(key)) return;
    const reference = {
      key,
      file: relativePath(absolute),
      line: lineNumber(source, index),
      kind,
      roles: usageRoles(source, index),
    };
    const identity = `${reference.file}:${reference.line}:${reference.key}:${reference.kind}`;
    if (seen.has(identity)) return;
    seen.add(identity);
    references.push(reference);
  }

  for (const absolute of walkSources(path.join(ROOT, "frontend", "src"))) {
    const source = fs.readFileSync(absolute, "utf8");
    const clean = stripComments(source);
    for (const match of clean.matchAll(/["']([A-Za-z][A-Za-z0-9_.-]*)["']/gu)) {
      const key = match[1];
      if (!knownKeys.has(key)) continue;
      const index = match.index ?? 0;
      const context = clean.slice(Math.max(0, index - 180), Math.min(clean.length, index + 180));
      const kind = /data-i18n(?:-[A-Za-z-]+)?\s*=|\bt\s*\(/u.test(context) ? "translation" : "literal";
      addReference(key, absolute, clean, index, kind);
    }
    for (const match of clean.matchAll(/\bt\(\s*`([A-Za-z][A-Za-z0-9_.-]*)\$\{/gu)) {
      dynamicPrefixes.add(match[1]);
    }
    for (const match of clean.matchAll(/data-i18n(?:-[A-Za-z-]+)?\s*=\s*["']([A-Za-z][A-Za-z0-9_.-]*)["']/gu)) {
      addReference(match[1], absolute, clean, match.index ?? 0, "attribute");
    }
  }

  return { references, dynamicPrefixes: [...dynamicPrefixes].sort() };
}

function isReferenced(key, graph) {
  return graph.references.some(reference => reference.key === key)
    || graph.dynamicPrefixes.some(prefix => key.startsWith(prefix));
}

function groupedValues(values, normalize = value => value) {
  const groups = new Map();
  for (const [key, value] of Object.entries(values)) {
    const groupKey = normalize(String(value));
    const group = groups.get(groupKey) || [];
    group.push(key);
    groups.set(groupKey, group);
  }
  return [...groups.values()].filter(group => group.length > 1);
}

function normalizedValue(value) {
  return value
    .toLocaleLowerCase()
    .replace(/\{[^}]+\}/gu, "{arg}")
    .replace(/[\s\p{P}\p{S}]+/gu, "");
}

function fragmentIssues(resources, locales) {
  const issues = [];
  for (const locale of locales) {
    for (const [key, value] of Object.entries(resources[locale])) {
      if (value !== value.trim() && !REVIEWED_FRAGMENT_KEYS.has(key)) {
        issues.push(`${locale}:${key} 含有首尾空白`);
      }
      if (/^[ \t]*[,:;：，；][ \t]*$/u.test(value)) {
        issues.push(`${locale}:${key} 是孤立标点片段`);
      }
      if ((key.endsWith("_suffix") || key.endsWith(".suffix") || key.endsWith("_prefix") || key.endsWith(".prefix"))
        && !REVIEWED_FRAGMENT_KEYS.has(key)
        && !REVIEWED_ROLE_EXCEPTIONS.has(key)) {
        issues.push(`${locale}:${key} 使用了未经审核的 prefix/suffix 角色`);
      }
    }
  }
  return issues;
}

function roleIssues(resources, graph) {
  const issues = [];
  for (const key of Object.keys(resources[Object.keys(resources)[0]])) {
    const references = graph.references.filter(reference => reference.key === key);
    if (key.endsWith("_badge") || key.endsWith(".badge")) {
      if (references.length > 0 && !references.some(reference => reference.roles.includes("badge"))) {
        issues.push(`${key} 声明为 badge，但调用点没有 badge 语境`);
      }
    }
    if (key.endsWith("_placeholder") || key.endsWith(".placeholder")) {
      if (references.length > 0 && !references.some(reference => reference.roles.includes("placeholder"))) {
        issues.push(`${key} 声明为 placeholder，但调用点没有 placeholder 语境`);
      }
    }
    if (key.endsWith("_suffix") || key.endsWith(".suffix")) {
      if (!REVIEWED_FRAGMENT_KEYS.has(key)) issues.push(`${key} 未列入 suffix 片段审核清单`);
    }
    if (key.endsWith("_prefix") || key.endsWith(".prefix")) {
      if (!REVIEWED_FRAGMENT_KEYS.has(key) && !REVIEWED_ROLE_EXCEPTIONS.has(key)) {
        issues.push(`${key} 未列入 prefix 片段审核清单`);
      }
    }
  }
  return issues;
}

function ambiguousKeys(resources) {
  return Object.keys(resources[Object.keys(resources)[0]])
    .filter(key => AMBIGUOUS_SUFFIXES.some(suffix => key.endsWith(suffix)))
    .sort();
}

function englishHardcodes() {
  const findings = [];
  const rules = [
    ["error", /new Error\(\s*(["'])([^"']+)\1/gu, 2],
    ["dom-text", /(?:textContent|innerHTML)\s*=\s*(["'])([^"']+)\1/gu, 2],
    ["attribute", /setAttribute\(\s*(["'])(?:aria-label|title|placeholder)\1\s*,\s*(["'])([^"']+)\2/gu, 3],
    ["markup-text", />[ \t\r\n]*([A-Za-z][A-Za-z0-9 &/().:+-]*)[ \t\r\n]*</gu, 1],
  ];
  for (const absolute of walkSources(path.join(ROOT, "frontend", "src"))) {
    const file = relativePath(absolute);
    const source = stripComments(fs.readFileSync(absolute, "utf8"));
    for (const [kind, pattern, valueIndex] of rules) {
      for (const match of source.matchAll(pattern)) {
        if (kind === "markup-text" && source[(match.index ?? 0) - 1] === "=") continue;
        const value = match[valueIndex].trim();
        if (kind === "markup-text" && /[<>=]|&&|\|\|/u.test(value)) continue;
        if (!/[A-Za-z]{3}/u.test(value) || ENGLISH_LITERAL_ALLOWLIST.has(value)) continue;
        if (kind === "markup-text" && TYPESCRIPT_DECLARATION_LINE.test(value)) continue;
        if (value.includes("<") || value.includes(">") || value.includes("${")) continue;
        findings.push({ file, line: lineNumber(source, match.index ?? 0), kind, value });
      }
    }
  }
  return findings;
}

test("i18n resource graph and semantic audit remain classified", () => {
  const locales = readLocaleIds();
  const resources = readResources(locales);
  const keys = Object.keys(resources[locales[0]]);
  const graph = collectReferenceGraph(resources);
  const unused = keys.filter(key => !isReferenced(key, graph)).sort();
  const unclassifiedUnused = unused.filter(key => (
    ![...REVIEWED_UNUSED_PREFIXES.keys()].some(prefix => key.startsWith(prefix))
    && !REVIEWED_UNUSED_KEYS.has(key)
  ));
  const staleUnusedBaseline = [...REVIEWED_UNUSED_KEYS]
    .filter(key => !unused.includes(key) || !keys.includes(key))
    .sort();
  const unknownDynamicPrefixes = graph.dynamicPrefixes
    .filter(prefix => !keys.some(key => key.startsWith(prefix)));
  const ambiguous = ambiguousKeys(resources);
  const roleIssuesFound = roleIssues(resources, graph);
  const fragments = fragmentIssues(resources, locales);
  const hardcodes = englishHardcodes();
  const exactDuplicates = groupedValues(resources[locales[0]]);
  const nearDuplicates = groupedValues(resources[locales[0]], normalizedValue);
  const roleCounts = Object.fromEntries(
    [...new Set(graph.references.flatMap(reference => reference.roles))]
      .sort()
      .map(role => [role, graph.references.filter(reference => reference.roles.includes(role)).length]),
  );

  console.log(
    `[i18n-audit] graph=${graph.references.length} refs/${new Set(graph.references.map(reference => reference.key)).size} keys, `
    + `dynamic=${graph.dynamicPrefixes.join(",") || "none"}, unused=${unused.length}, `
    + `exact-duplicates=${exactDuplicates.length}, near-duplicates=${nearDuplicates.length}, `
    + `ambiguous-roles=${ambiguous.length}, english-hardcodes=${hardcodes.length}, roles=${JSON.stringify(roleCounts)}`,
  );

  assert.deepEqual(unknownDynamicPrefixes, [], `动态资源前缀没有对应资源：${unknownDynamicPrefixes.join(", ")}`);
  assert.deepEqual(unclassifiedUnused, [], `发现未分类的未引用资源：${unclassifiedUnused.join(", ")}`);
  assert.deepEqual(staleUnusedBaseline, [], `未引用资源基线已过期，请移除已恢复或已删除的条目：${staleUnusedBaseline.join(", ")}`);
  assert.deepEqual(ambiguous, [...REVIEWED_AMBIGUOUS_KEYS].sort(), "发现未审核的模糊 role key，请先完成语义审计");
  assert.deepEqual(roleIssuesFound, [], `资源 role 与调用语境不一致：${roleIssuesFound.join("；")}`);
  assert.deepEqual(fragments, [], `发现未经审核的资源片段：${fragments.join("；")}`);
  assert.deepEqual(hardcodes, [], `发现疑似用户可见英文硬编码：${hardcodes.map(item => `${item.file}:${item.line} ${item.value}`).join("；")}`);
});
