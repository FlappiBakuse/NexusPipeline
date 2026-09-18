import fs from "node:fs";
import path from "node:path";

import { expectedTestSelectors, globToRegExp } from "./ci-domains.mjs";

const IGNORED_DIRECTORIES = new Set([".git", "node_modules", "dist", "bin", "obj", ".artifacts"]);

function normalizePath(value) {
  if (typeof value !== "string") return "";
  const normalized = value.trim().replaceAll("\\", "/").replace(/^\.\//u, "");
  if (!normalized || normalized.startsWith("/") || /^[A-Za-z]:\//u.test(normalized)) return "";
  if (normalized.split("/").some(segment => segment === "..")) return "";
  return normalized;
}

function walkFiles(root) {
  if (!root || !fs.existsSync(root)) return [];
  const files = [];
  const visit = directory => {
    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
      if (entry.isDirectory() && IGNORED_DIRECTORIES.has(entry.name)) continue;
      const fullPath = path.join(directory, entry.name);
      if (entry.isDirectory()) visit(fullPath);
      else if (entry.isFile()) files.push(fullPath);
    }
  };
  visit(root);
  return files;
}

function sourceRoots(root) {
  const resolvedRoot = path.resolve(root);
  const roots = [{ root: resolvedRoot, prefix: "" }];
  const siblingPlugins = path.resolve(resolvedRoot, "..", "NexusPipeline-Plugins");
  const nestedPlugins = path.join(resolvedRoot, "NexusPipeline-Plugins");
  if (fs.existsSync(siblingPlugins)) roots.push({ root: siblingPlugins, prefix: "NexusPipeline-Plugins/" });
  if (fs.existsSync(nestedPlugins)) roots.push({ root: nestedPlugins, prefix: "NexusPipeline-Plugins/" });
  return roots;
}

function candidateFiles(root) {
  return sourceRoots(root).flatMap(({ root: sourceRoot, prefix }) => walkFiles(sourceRoot).map(file => ({
    file,
    logicalPath: `${prefix}${path.relative(sourceRoot, file).replaceAll("\\", "/")}`,
  })));
}

/**
 * Expand the selectors recorded in the execution plan into the concrete files
 * visible in the checkout. The plan keeps selectors for authoring stability and
 * this list for the bidirectional runner/summary reconciliation.
 */
export function expandTestSelectors(selectors, { root = process.cwd() } = {}) {
  const candidates = candidateFiles(root);
  const expanded = [];
  const seen = new Set();
  for (const selector of Array.isArray(selectors) ? selectors : []) {
    const normalizedSelector = normalizePath(selector);
    if (!normalizedSelector) continue;
    const matcher = globToRegExp(normalizedSelector);
    for (const candidate of candidates) {
      if (!matcher.test(candidate.logicalPath) || seen.has(candidate.logicalPath)) continue;
      seen.add(candidate.logicalPath);
      expanded.push(candidate.logicalPath);
    }
  }
  return expanded.sort((left, right) => left.localeCompare(right));
}

export function expectedTestFiles(kind, group, options = {}) {
  // `tests/run.mjs ...` entries are runner commands kept in the selector
  // registry. Their concrete test files are represented by the adjacent file
  // globs (or by the system group definitions), so a command string must not
  // become a fake filesystem path.
  const selectors = expectedTestSelectors(kind, group).filter(selector => !/\s/u.test(selector));
  const expanded = expandTestSelectors(selectors, options);
  // The official plugin contract is executed in a separate repository checkout
  // on some host jobs. Keep its exact logical identity in the plan even when
  // that checkout is not present, so plan validation remains deterministic.
  const crossRepositoryFiles = selectors
    .map(normalizePath)
    .filter(selector => selector.startsWith("NexusPipeline-Plugins/") && !/[?*[\]{}]/u.test(selector));
  return [...new Set([...expanded, ...crossRepositoryFiles])].sort((left, right) => left.localeCompare(right));
}

export function pathMatchesSelector(value, selectors) {
  const normalized = normalizePath(value);
  if (!normalized) return false;
  return (Array.isArray(selectors) ? selectors : []).some(selector => {
    const normalizedSelector = normalizePath(selector);
    return normalizedSelector && globToRegExp(normalizedSelector).test(normalized);
  });
}

export function normalizeTestPath(value) {
  return normalizePath(value);
}
