import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { baseParse } from "@vue/compiler-dom";
import { parse } from "@vue/compiler-sfc";
import ts from "typescript";

export const FRONTEND_BOUNDARY_SCHEMA_VERSION = 1;

const PRIVATE_VUE_FIELDS = /\._(?:instance|mount|app|vnode|slots)\b/gu;
const IMPORT_SPECIFIER = /(?:\bfrom\s*|\bimport\s*\(\s*)["']([^"']+)["']/gu;

const EXCEPTIONS = [
  {
    ruleId: "navigation-backdrop",
    path: "frontend/src/app/App.vue",
    token: "shell.close_navigation",
    classification: "semantic-exception",
    reason: "移动端导航遮罩是无内容的点击目标，承担关闭抽屉的背景层语义。",
  },
  {
    ruleId: "hidden-payload-input",
    path: "frontend/src/features/history/components/RangePicker.vue",
    token: "type=\"hidden\"",
    classification: "semantic-exception",
    reason: "历史日期范围的隐藏 input 仅承载受控查询值，不提供用户交互。",
  },
  {
    ruleId: "native-script-file-picker",
    path: "frontend/src/features/scripts/ScriptEditorModal.vue",
    token: "type=\"file\"",
    classification: "business-specific",
    reason: "判定脚本导入需要保留 File 对象和现有导入错误边界；按钮动作已使用 NxpButton。",
  },
  {
    ruleId: "native-script-file-picker",
    path: "frontend/src/features/scripts/ScriptEditorModal.vue",
    token: "document.createElement(\"input\")",
    classification: "business-specific",
    reason: "判定脚本导入需要保留 File 对象和现有导入错误边界；按钮动作已使用 NxpButton。",
  },
  {
    ruleId: "native-avatar-file-picker",
    path: "frontend/src/features/users/UsersPage.vue",
    token: "image/png,image/jpeg,image/webp",
    classification: "business-specific",
    reason: "用户头像上传由用户实体页面保留 File 对象并同步列表与管理草稿。",
  },
  {
    ruleId: "native-avatar-file-picker",
    path: "frontend/src/features/users/UsersPage.vue",
    token: "document.createElement(\"input\")",
    classification: "business-specific",
    reason: "用户头像上传由用户实体页面保留 File 对象并同步列表与管理草稿。",
  },
  {
    ruleId: "limits-alert-dialog-actions",
    path: "frontend/src/platform/limits.ts",
    token: "document.createElement(\"button\")",
    classification: "semantic-exception",
    reason: "启动约束警告需要在 Vue 应用外建立 alertdialog、焦点陷阱和恢复焦点；动作节点由该服务统一销毁。",
  },
];

function normalizePath(value) {
  return String(value || "").replaceAll("\\", "/").replace(/^\.\//u, "");
}

function lineAt(source, offset) {
  return source.slice(0, offset).split("\n").length;
}

function columnAt(source, offset) {
  const lineStart = source.lastIndexOf("\n", offset - 1) + 1;
  return offset - lineStart + 1;
}

function sourceEntries(sources) {
  if (!sources) return [];
  if (Array.isArray(sources)) return sources.map(item => [normalizePath(item.path), String(item.content ?? "")]);
  return Object.entries(sources).map(([file, content]) => [normalizePath(file), String(content ?? "")]);
}

const PRODUCTION_EXTENSIONS = new Set([".vue", ".ts", ".js", ".mjs", ".css", ".html"]);
const GENERATED_DIRECTORIES = new Set(["node_modules", "dist", "bin", "obj", ".git", ".vite", "coverage"]);

export function readProductionSources(root) {
  const frontendRoot = path.join(root, "frontend", "src");
  const files = [];
  const walk = directory => {
    if (!fs.existsSync(directory)) return;
    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
      if (GENERATED_DIRECTORIES.has(entry.name)) continue;
      const fullPath = path.join(directory, entry.name);
      if (entry.isDirectory()) walk(fullPath);
      else if (PRODUCTION_EXTENSIONS.has(path.extname(entry.name).toLowerCase())
        && !entry.name.endsWith(".test.ts")
        && !entry.name.endsWith(".test.js")
        && !entry.name.endsWith(".test.mjs")
        && !entry.name.endsWith(".spec.ts")
        && !entry.name.endsWith(".spec.js")
        && !entry.name.endsWith(".spec.mjs")) {
        files.push([normalizePath(path.relative(root, fullPath)), fs.readFileSync(fullPath, "utf8")]);
      }
    }
  };
  walk(frontendRoot);
  return files.sort(([left], [right]) => left.localeCompare(right));
}

function templateElements(filePath, source) {
  const elements = [];
  const visit = node => {
    if (node.type === 1) elements.push(node);
    for (const child of node.children || []) visit(child);
  };
  if (filePath.endsWith(".vue")) {
    const { descriptor } = parse(source);
    if (descriptor.template?.ast) visit(descriptor.template.ast);
  } else if (filePath.endsWith(".html")) {
    visit(baseParse(source));
  }
  return elements;
}

function addFinding(findings, { path: filePath, source, offset, ruleId, kind, token, severity = "error", classification = "violation", reason = "" }) {
  findings.push({
    ruleId,
    path: filePath,
    line: lineAt(source, offset),
    column: columnAt(source, offset),
    kind,
    token,
    severity,
    classification,
    reason,
  });
}

function exceptionFor(filePath, token) {
  return EXCEPTIONS.find(exception => exception.path === filePath && token.includes(exception.token));
}

function scanPrivateFields(filePath, source, findings) {
  for (const match of source.matchAll(PRIVATE_VUE_FIELDS)) {
    if (filePath === "frontend/src/ui/register.ts") {
      addFinding(findings, {
        path: filePath,
        source,
        offset: match.index,
        ruleId: "private-vue-adapter",
        kind: "vue-internal-field",
        token: match[0],
        severity: "info",
        classification: "reviewed-adapter",
        reason: "唯一集中在 Custom Element Light DOM 适配器内，受注册器契约测试保护。",
      });
    } else {
      addFinding(findings, {
        path: filePath,
        source,
        offset: match.index,
        ruleId: "private-vue-field-leak",
        kind: "vue-internal-field",
        token: match[0],
        reason: "Vue 私有字段只能由唯一的公开元素适配器访问。",
      });
    }
  }
}

function scanImportBoundary(filePath, source, findings, isPlugin) {
  if (!isPlugin) return;
  for (const match of source.matchAll(IMPORT_SPECIFIER)) {
    const specifier = match[1].replaceAll("\\", "/");
    if (/(?:^|\/)(?:frontend\/src|NexusPipeline\/(?:frontend\/)?src)\/(?:ui|features|platform|stores|app|plugin-bridge|styles)(?:\/|$)/u.test(specifier)) {
      addFinding(findings, {
        path: filePath,
        source,
        offset: match.index,
        ruleId: "plugin-private-host-import",
        kind: "import",
        token: specifier,
        reason: "插件只能消费注册的 nxp-* 元素和 Frontend API，不能直接导入宿主 SFC。",
      });
    }
  }
}

function scanTemplateControls(filePath, source, findings) {
    for (const node of templateElements(filePath, source)) {
      const tag = node.tag.toLowerCase();
      if (!["button", "input", "select", "textarea"].includes(tag)) continue;
      const offset = node.loc.start.offset;
      const token = `<${node.tag} ${node.props.map(prop => prop.loc.source).join(" ")}>`;
      const type = node.props.find(prop => prop.type === 6 && prop.name === "type")?.value?.content?.toLowerCase() || "";
      if (tag === "input" && type === "hidden") {
        addFinding(findings, { path: filePath, source, offset, ruleId: "hidden-native-input", kind: "interactive-element", token, severity: "info", classification: "semantic-exception", reason: "隐藏 input 是受控载荷，不是用户交互控件。" });
      } else if (filePath.startsWith("frontend/src/ui/")) {
        addFinding(findings, { path: filePath, source, offset, ruleId: "public-ui-native-control", kind: "interactive-element", token, severity: "info", classification: "public-ui", reason: "公共 UI 元件内部保留原生控件实现。" });
      } else {
        const exception = exceptionFor(filePath, token);
        if (exception) addFinding(findings, { path: filePath, source, offset, ruleId: exception.ruleId, kind: "interactive-element", token, severity: "info", classification: exception.classification, reason: exception.reason });
        else addFinding(findings, { path: filePath, source, offset, ruleId: "native-interactive-bypass", kind: "interactive-element", token, reason: `业务层 ${tag} 应使用现有公开 UI 元件，或先登记精确语义例外。` });
      }
    }
}

function scanDynamicControls(filePath, source, findings) {
  if (!/[.]vue$|[.](?:ts|js|mjs)$/u.test(filePath)) return;
  const scripts = filePath.endsWith(".vue") ? (() => {
    const { descriptor } = parse(source);
    return [descriptor.script, descriptor.scriptSetup].filter(Boolean).map(block => ({ text: block.content, offset: block.loc.start.offset }));
  })() : [{ text: source, offset: 0 }];
  const matches = [];
  for (const script of scripts) {
    const ast = ts.createSourceFile(filePath + ".ts", script.text, ts.ScriptTarget.Latest, true);
    const visit = node => {
      if (ts.isCallExpression(node) && node.arguments.length > 0 && ts.isStringLiteralLike(node.arguments[0])
        && ["button", "input", "select", "textarea"].includes(node.arguments[0].text)
        && /^(?:document\.createElement|textElement|h|createElement)$/u.test(node.expression.getText(ast))) {
        const match = [node.getText(ast)];
        match.index = script.offset + node.getStart(ast);
        matches.push(match);
      }
      ts.forEachChild(node, visit);
    };
    visit(ast);
  }
  for (const match of matches) {
    const token = match[0];
    if (filePath.startsWith("frontend/src/ui/")) {
      addFinding(findings, { path: filePath, source, offset: match.index, ruleId: "public-ui-dynamic-control", kind: "dynamic-interactive-element", token, severity: "info", classification: "public-ui", reason: "公共 UI 元件内部可按需要创建原生控件。" });
      continue;
    }
    const exception = exceptionFor(filePath, token) || (filePath === "frontend/src/platform/limits.ts" ? EXCEPTIONS.find(item => item.ruleId === "limits-alert-dialog-actions") : null);
    if (exception) addFinding(findings, { path: filePath, source, offset: match.index, ruleId: exception.ruleId, kind: "dynamic-interactive-element", token, severity: "info", classification: exception.classification, reason: exception.reason });
    else addFinding(findings, { path: filePath, source, offset: match.index, ruleId: "native-dynamic-interactive-bypass", kind: "dynamic-interactive-element", token, reason: "动态控件创建必须使用公开元素或登记精确的文件/符号语义例外。" });
  }
}

function scanInteractiveRoles(filePath, source, findings) {
  if (!/[.](?:vue|html)$/u.test(filePath)) return;
  if (filePath.startsWith("frontend/src/ui/")) return;
  const rolePattern = /<([a-z][a-z0-9-]*)\b([^>]*)>/giu;
  for (const match of source.matchAll(rolePattern)) {
    if (/^[A-Z]/u.test(match[1])) continue;
    const tag = match[1].toLowerCase();
    const attributes = match[2] || "";
    if (tag === "button" || tag === "input" || tag === "select" || tag === "textarea" || tag.startsWith("nxp-")) continue;
    const role = /\brole\s*=\s*["'](button|checkbox|combobox|option|radio|switch|tab)["']/iu.exec(attributes);
    if (!role) continue;
    const token = role[0];
    if (exceptionFor(filePath, token)) continue;
    addFinding(findings, { path: filePath, source, offset: match.index, ruleId: "interactive-role-bypass", kind: "interactive-role", token, reason: "带交互 ARIA role 的节点必须属于公开交互元件或精确语义例外。" });
  }
}

function styleBlocks(filePath, source) {
  if (filePath.endsWith(".css")) return [{ text: source, offset: 0 }];
  if (!filePath.endsWith(".vue")) return [];
  const { descriptor } = parse(source);
  return descriptor.styles.map(block => ({ text: block.content, offset: block.loc.start.offset }));
}

function styleSelectorIsDescendantOfPublicControl(selector) {
  const normalized = selector.replace(/\s+/gu, " ").trim();
  if (!/\b(?:nxp-[A-Za-z0-9_-]+|nxp-[a-z0-9-]+)\b/iu.test(normalized)) return false;
  const firstToken = normalized.split(/\s+|>|\+|~/u)[0] || "";
  return !/^(?:\.?nxp-[A-Za-z0-9_-]+|nxp-[A-Za-z0-9_-]+)(?::|\.|\[|$)/u.test(firstToken);
}

function scanStyleSelectors(filePath, source, findings, offsetBase = 0) {
  if (filePath.startsWith("frontend/src/ui/")) return;
  const withoutComments = source.replace(/\/\*[\s\S]*?\*\//gu, match => " ".repeat(match.length));
  for (const match of withoutComments.matchAll(/([^{}]+)\{/gu)) {
    const rawSelector = match[1].trim();
    if (!rawSelector || rawSelector.startsWith("@")) continue;
    for (const selector of rawSelector.split(",").map(value => value.trim()).filter(Boolean)) {
      if (!styleSelectorIsDescendantOfPublicControl(selector)) continue;
      const localOffset = match.index + match[1].indexOf(selector);
      addFinding(findings, {
        path: filePath,
        source: source,
        offset: offsetBase + localOffset,
        ruleId: "private-style-selector",
        kind: "style-selector",
        token: selector,
        reason: "业务样式只能定位业务 wrapper；公共 nxp-* 元件的内部节点由元件自身样式和公开能力负责。",
      });
    }
  }
}

export function scanSource(filePath, source, { plugin = false } = {}) {
  const normalized = normalizePath(filePath);
  const findings = [];
  scanPrivateFields(normalized, source, findings);
  scanImportBoundary(normalized, source, findings, plugin);
  scanTemplateControls(normalized, source, findings);
  scanDynamicControls(normalized, source, findings);
  scanInteractiveRoles(normalized, source, findings);
  for (const block of styleBlocks(normalized, source)) scanStyleSelectors(normalized, block.text, findings, block.offset);
  return findings;
}

export function scanFrontendBoundaries({ root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), ".."), sources = null, pluginSources = null, pluginsRoot = null } = {}) {
  const hostEntries = sources ? sourceEntries(sources) : readProductionSources(root);
  const pluginEntries = sourceEntries(pluginSources);
  if (pluginsRoot) {
    for (const family of ["general", "specialized"]) {
      const directory = path.join(pluginsRoot, "plugins", family);
      if (!fs.existsSync(directory)) throw new Error(`缺少插件源码目录：${directory}`);
      for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
        if (!entry.isDirectory()) continue;
        for (const [file, content] of readProductionSources(path.join(directory, entry.name))) {
          pluginEntries.push([`plugins/${family}/${entry.name}/${file}`, content]);
        }
      }
    }
  }
  const findings = [
    ...hostEntries.flatMap(([filePath, source]) => scanSource(filePath, source)),
    ...pluginEntries.flatMap(([filePath, source]) => scanSource(filePath, source, { plugin: true })),
  ];
  return {
    schemaVersion: FRONTEND_BOUNDARY_SCHEMA_VERSION,
    filesScanned: hostEntries.length + pluginEntries.length,
    findings,
    issues: findings.filter(finding => finding.severity === "error"),
    ok: findings.every(finding => finding.severity !== "error"),
  };
}

function main(argv) {
  const rootIndex = argv.indexOf("--root");
  const pluginsIndex = argv.indexOf("--plugins-root");
  const report = scanFrontendBoundaries({ root: rootIndex >= 0 ? argv[rootIndex + 1] : undefined, pluginsRoot: pluginsIndex >= 0 ? argv[pluginsIndex + 1] : null });
  for (const finding of argv.includes("--quiet") ? report.issues : report.findings) {
    const prefix = finding.severity === "error" ? "错误" : "例外";
    console.log(`[前端边界][${prefix}] ${finding.path}:${finding.line}:${finding.column} ${finding.ruleId} ${finding.token}`);
    if (finding.reason) console.log(`  ${finding.reason}`);
  }
  console.log(`[前端边界] 扫描 ${report.filesScanned} 个生产文件，${report.issues.length} 个未解释问题。`);
  return report.ok ? 0 : 1;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  process.exitCode = main(process.argv.slice(2));
}
