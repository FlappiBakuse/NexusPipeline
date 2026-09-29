import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import ts from "typescript";
import { parse as parseVue } from "@vue/compiler-sfc";

const defaultRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

function sourceFiles(directory) {
  return fs.readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    const full = path.join(directory, entry.name);
    if (entry.isDirectory()) return sourceFiles(full);
    return /\.(ts|tsx|vue)$/.test(entry.name) && !/\.(?:test|spec|d)\.ts$/.test(entry.name) ? [full] : [];
  });
}

function resolveImport(root, from, specifier, compilerOptions) {
  const resolved = ts.resolveModuleName(specifier, from, compilerOptions, ts.sys).resolvedModule?.resolvedFileName;
  if (resolved) return path.resolve(resolved);
  const aliases = { "@bridge/": "src/plugin-bridge/", "@/": "src/", "@platform/": "src/platform/" };
  const alias = Object.entries(aliases).find(([prefix]) => specifier.startsWith(prefix));
  const base = alias ? path.join(root, alias[1], specifier.slice(alias[0].length))
    : specifier.startsWith(".") ? path.resolve(path.dirname(from), specifier) : null;
  if (!base) return null;
  return [base, `${base}.ts`, `${base}.tsx`, `${base}.vue`, path.join(base, "index.ts")]
    .find(candidate => fs.existsSync(candidate)) ?? null;
}

export function checkFrontendArchitecture(root = defaultRoot) {
  root = path.resolve(root);
  const configPath = ts.findConfigFile(root, ts.sys.fileExists, "tsconfig.json");
  if (!configPath) throw new Error("Frontend tsconfig.json is required");
  const raw = ts.readConfigFile(configPath, ts.sys.readFile);
  if (raw.error) throw new Error(ts.flattenDiagnosticMessageText(raw.error.messageText, "\n"));
  const config = ts.parseJsonConfigFileContent(raw.config, ts.sys, root);
  if (config.errors.length) throw new Error("Invalid TypeScript configuration");
  const bridgeRoot = path.join(root, "src", "plugin-bridge") + path.sep;
  const publicFacade = path.join(bridgeRoot, "index.ts");
  const violations = [];
  for (const file of sourceFiles(path.join(root, "src"))) {
    const relative = path.relative(root, file).replaceAll("\\", "/");
    if (!/^src\/(app|features|ui)\//.test(relative)) continue;
    const contents = fs.readFileSync(file, "utf8");
    const blocks = file.endsWith(".vue") ? (() => {
      const parsed = parseVue(contents, { filename: file });
      if (parsed.errors.length) throw new Error(`Invalid Vue SFC: ${relative}`);
      return [parsed.descriptor.script, parsed.descriptor.scriptSetup].filter(Boolean)
        .map(block => ({ code: block.content, offset: block.loc.start.line - 1 }));
    })() : [{ code: contents, offset: 0 }];
    for (const block of blocks) {
      const syntax = ts.createSourceFile(file, block.code, ts.ScriptTarget.Latest, true,
        file.endsWith(".tsx") ? ts.ScriptKind.TSX : ts.ScriptKind.TS);
      const visit = node => {
        let specifier = null;
        if ((ts.isImportDeclaration(node) || ts.isExportDeclaration(node)) && node.moduleSpecifier
            && ts.isStringLiteral(node.moduleSpecifier)) specifier = node.moduleSpecifier.text;
        if (ts.isCallExpression(node) && node.expression.kind === ts.SyntaxKind.ImportKeyword
            && node.arguments.length === 1 && ts.isStringLiteral(node.arguments[0])) specifier = node.arguments[0].text;
        if (specifier) {
          const target = resolveImport(root, file, specifier, config.options);
          if (target && target.startsWith(bridgeRoot) && target !== publicFacade)
            violations.push({ ruleId: "A05", file: relative,
              line: block.offset + syntax.getLineAndCharacterOfPosition(node.getStart(syntax)).line + 1,
              target: path.relative(root, target).replaceAll("\\", "/") });
        }
        ts.forEachChild(node, visit);
      };
      visit(syntax);
    }
  }
  return { schemaVersion: 1, checkedRules: ["A05"], status: violations.length ? "FAIL" : "PASS", violations };
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const root = process.argv[2] && process.argv[2] !== "--report" ? process.argv[2] : defaultRoot;
  const reportArg = process.argv.indexOf("--report");
  const reportPath = reportArg < 0 ? null : process.argv[reportArg + 1];
  const report = checkFrontendArchitecture(root);
  const encoded = JSON.stringify(report, null, 2) + "\n";
  if (reportPath) {
    fs.mkdirSync(path.dirname(path.resolve(reportPath)), { recursive: true });
    fs.writeFileSync(reportPath, encoded);
  } else process.stdout.write(encoded);
  if (report.status !== "PASS") process.exitCode = 1;
}
