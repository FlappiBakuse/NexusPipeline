import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const LOCALES = ["zh-CN", "en-US"];
const PLACEHOLDER = /\{([A-Za-z][A-Za-z0-9_.-]*)\}/gu;
const SOURCE_EXTENSIONS = new Set([".js", ".mjs", ".html"]);

function read(relativePath) {
  return fs.readFileSync(path.join(ROOT, relativePath), "utf8");
}

function readWebResources() {
  return Object.fromEntries(LOCALES.map(locale => [locale, JSON.parse(read(`wwwroot/i18n/${locale}.json`))]));
}

function placeholders(value) {
  return [...String(value).matchAll(PLACEHOLDER)]
    .map(match => match[1])
    .sort();
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

function collectDirectReferences() {
  const references = [];
  const pattern = /\bt\([^\)\r\n]*?["']([A-Za-z][A-Za-z0-9_.-]*)["']/gu;
  for (const absolute of walkSources(path.join(ROOT, "wwwroot"))) {
    const relativePath = path.relative(ROOT, absolute).replaceAll(path.sep, "/");
    const source = fs.readFileSync(absolute, "utf8");
    for (const match of source.matchAll(pattern)) references.push({ relativePath, key: match[1] });
  }
  return references;
}

function registryIds(registry) {
  return registry.supported.map(item => typeof item === "string" ? item : item.id);
}

test("host and web locale registries remain synchronized", () => {
  const web = JSON.parse(read("wwwroot/i18n/locales.json"));
  const host = JSON.parse(read("src/Localization/Resources/locales.json"));
  assert.equal(web.default, host.default);
  assert.deepEqual(registryIds(web), registryIds(host));
  assert.deepEqual(registryIds(web), LOCALES);
});

test("critical i18n bindings keep their business semantics", () => {
  const resources = readWebResources();
  const zh = resources["zh-CN"];
  const en = resources["en-US"];

  assert.equal(zh["users.global.run_days.help"], zh["users.binding.run_days.help"]);
  assert.equal(en["users.global.run_days.help"], en["users.binding.run_days.help"]);
  assert.equal(zh["users.global.run_days.placeholder"], "-1 永久；0 停止");
  assert.equal(en["users.global.run_days.placeholder"], "-1 forever; 0 stop");
  assert.doesNotMatch(zh["users.global.run_days.help"], /星期|周一|周日|1-7/u);
  assert.doesNotMatch(en["users.global.run_days.help"], /weekday|Monday|Sunday|1-7/iu);

  assert.equal(en["api.error.dispatch_failed"], "Failed to start the run");
  assert.notEqual(en["api.error.dispatch_failed"], en["dispatch.load.failed"]);
  assert.equal(zh["dashboard.active_tasks.count"], "{count} 个活动任务");
  assert.equal(en["dashboard.active_tasks.count"], "{count} active tasks");
  assert.deepEqual(placeholders(zh["dispatch.summary.items"]), ["done", "total"]);
  assert.deepEqual(placeholders(en["dispatch.summary.items"]), ["done", "total"]);
  assert.deepEqual(placeholders(zh["common.status.queue_complete"]), ["countdown", "queueName"]);
  assert.deepEqual(placeholders(en["common.status.queue_complete"]), ["countdown", "queueName"]);

  const removedKeys = [
    "common.check_failed",
    "common.until_start",
    "common.waiting_because",
    "dashboard.active_tasks",
    "dispatch.summary.items_suffix",
    "history.log.tail_summary",
    "history.reason_separator",
    "queues.after_completion",
    "settings.diagnostics.all_passed_suffix",
    "settings.diagnostics.attention_summary",
    "users.global_general_copy",
    "users.global_management",
    "users.global_management_button",
    "users.global_notification_copy",
    "users.global_override_copy",
    "users.global_settings_saved",
    "users.global_smtp_help",
    "users.run_days_help",
    "users.run_days_placeholder",
  ];
  for (const key of removedKeys) {
    assert.equal(Object.hasOwn(zh, key), false, `removed key remains in zh-CN: ${key}`);
    assert.equal(Object.hasOwn(en, key), false, `removed key remains in en-US: ${key}`);
  }
});

test("localized values are complete templates at fragment migration boundaries", () => {
  const resources = readWebResources();
  for (const locale of LOCALES) {
    for (const [key, value] of Object.entries(resources[locale])) {
      assert.equal(value, value.trim(), `${locale} value has boundary whitespace: ${key}`);
    }
  }

  const directReferences = collectDirectReferences();
  const requiredReferences = {
    "wwwroot/views/dashboard.js": ["dashboard.active_tasks.count"],
    "wwwroot/views/dispatch.js": ["dispatch.summary.items", "dispatch.status_update_failed"],
    "wwwroot/views/history.js": ["history.failure_reason", "history.log.lines_summary", "history.log.lines_summary.tail"],
    "wwwroot/views/users/global-management.js": [
      "users.global.general.help",
      "users.global.notification.enabled_help",
      "users.global.notification.smtp_help",
      "users.global.run_days.help",
      "users.global.run_days.placeholder",
      "users.global.saved",
      "users.global.title",
    ],
    "wwwroot/views/users/shared.js": ["users.global.open_action", "users.global.title"],
    "wwwroot/views/users/user-management.js": ["users.binding.global_override.help", "users.binding.run_days.help"],
  };
  for (const [relativePath, keys] of Object.entries(requiredReferences)) {
    for (const key of keys) {
      assert.ok(
        directReferences.some(reference => reference.relativePath === relativePath && reference.key === key),
        `critical locale key is not referenced at its intended call site: ${relativePath} -> ${key}`,
      );
    }
  }

  for (const key of ["settings.diagnostics.overview_attention", "settings.diagnostics.overview_clear"]) {
    assert.match(read("wwwroot/views/settings.js"), new RegExp(`['"]${key.replaceAll(".", "\\.")}['"]`, "u"));
  }
});
