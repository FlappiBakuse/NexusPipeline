import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { createRequire } from 'node:module';

const args = process.argv.slice(2);
const value = name => {
  const index = args.indexOf(name);
  if (index < 0 || !args[index + 1]) throw new Error('Missing ' + name);
  return args[index + 1];
};
const root = path.resolve(value('--root')), modules = path.resolve(value('--modules'));
const require = createRequire(path.join(modules, 'package.json'));
const ts = require('typescript'), vue = require('@vue/compiler-sfc');
const configPath = path.join(root, 'apps/web/tsconfig.json');
const config = ts.readConfigFile(configPath, ts.sys.readFile);
if (config.error) throw new Error('Invalid TypeScript configuration');
const options = ts.parseJsonConfigFileContent(config.config, ts.sys, path.dirname(configPath)).options;
const owners = args.flatMap((item, index) => item === '--owner' ? [path.resolve(root, args[index + 1])] : []);
const files = [], edges = [], errors = [], inputs = [];
const relative = full => path.relative(root, full).split(path.sep).join('/');
const hash = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
function walk(directory) {
  for (const item of fs.readdirSync(directory, { withFileTypes: true })) {
    const full = path.join(directory, item.name);
    if (item.isSymbolicLink()) throw new Error('Linked frontend source');
    if (item.isDirectory()) walk(full);
    else if (/\.(?:ts|tsx|js|mjs|vue)$/.test(item.name) && (!owners.length || owners.some(owner => full === owner || full.startsWith(owner + path.sep)))) files.push(full);
  }
}
walk(path.join(root, 'apps/web/src'));
walk(path.join(root, 'sdk/frontend/src'));
if (!files.length) throw new Error('Zero frontend architecture inputs');
function resolve(specifier, source) {
  const resolved = ts.resolveModuleName(specifier, source, options, ts.sys).resolvedModule;
  if (resolved && !resolved.resolvedFileName.includes('node_modules')) return path.resolve(resolved.resolvedFileName);
  const candidates = [];
  if (specifier.startsWith('.')) candidates.push(path.resolve(path.dirname(source), specifier));
  else for (const [alias, targets] of Object.entries(options.paths || {})) {
    const [prefix, suffix = ''] = alias.split('*');
    if (!specifier.startsWith(prefix) || !specifier.endsWith(suffix) || !alias.includes('*') && specifier !== alias) continue;
    const middle = alias.includes('*') ? specifier.slice(prefix.length, suffix ? -suffix.length : undefined) : '';
    for (const target of targets) candidates.push(path.resolve(options.baseUrl || path.dirname(configPath), target.replace('*', middle)));
  }
  for (const candidate of candidates)
    for (const name of [candidate, candidate.replace(/\.js$/, '.ts'), candidate + '.ts', candidate + '.vue', path.join(candidate, 'index.ts')])
      if (fs.existsSync(name) && fs.statSync(name).isFile()) return name;
  if (specifier.startsWith('.') || candidates.length) throw new Error(relative(source) + ': unresolved internal import ' + specifier);
  return undefined;
}
function rule(source, target, typeOnly) {
  const base = 'apps/web/src/';
  if (source.startsWith('sdk/') && target.startsWith('apps/')) return 'SDK_UPWARD';
  if (!target.startsWith(base)) return undefined;
  const destination = target.slice(base.length);
  if (source.startsWith(base + 'control/') && /^(pages|components|extensions|app)\//.test(destination)) return 'CONTROL_DIRECTION';
  if (source.startsWith(base + 'ui/') && (/^(pages|components|extensions|app)\//.test(destination) ||
      destination.startsWith('control/') && destination !== 'control/identity.ts')) return 'UI_DIRECTION';
  if (/^apps\/web\/src\/(pages|components)\//.test(source) && destination.startsWith('extensions/') && destination !== 'extensions/ExtensionOutlet.vue')
    return 'PUBLIC_SURFACE_ONLY';
  if (source.startsWith(base + 'app/') && destination.startsWith('extensions/')) {
    if (destination === 'extensions/ExtensionOutlet.vue') return undefined;
    if (source === base + 'app/App.vue' && ['frontend-registry.ts', 'frontend-loader.ts', 'registry-context.ts', 'SurfaceOutlet.vue']
      .some(name => destination === 'extensions/' + name)) return undefined;
    if (source === base + 'app/AppNavigation.vue' && destination === 'extensions/frontend-registry.ts' && typeOnly) return undefined;
    return 'APP_COMPOSITION_ONLY';
  }
  return undefined;
}
for (const file of files) {
  const bytes = fs.readFileSync(file), text = bytes.toString('utf8');
  const source = relative(file);
  inputs.push({ path: source, sha256: hash(bytes) });
  let sections = [{ content: text, line: 0 }];
  if (file.endsWith('.vue')) {
    const parsed = vue.parse(text, { filename: file });
    if (parsed.errors.length) throw new Error(source + ': invalid Vue source');
    sections = [parsed.descriptor.script, parsed.descriptor.scriptSetup].filter(Boolean)
      .map(item => ({ content: item.content, line: item.loc.start.line - 1 }));
  }
  for (const section of sections) {
    const tree = ts.createSourceFile(file, section.content, ts.ScriptTarget.Latest, true, ts.ScriptKind.TS);
    if (tree.parseDiagnostics.length) throw new Error(source + ': invalid TypeScript source');
    function visit(node) {
      let literal, typeOnly = false;
      if (ts.isImportDeclaration(node) || ts.isExportDeclaration(node)) {
        literal = node.moduleSpecifier;
        typeOnly = node.isTypeOnly === true || node.importClause?.isTypeOnly === true;
      } else if (ts.isCallExpression(node) && node.expression.kind === ts.SyntaxKind.ImportKeyword && node.arguments.length === 1)
        literal = node.arguments[0];
      else if (ts.isImportTypeNode(node) && ts.isLiteralTypeNode(node.argument)) { literal = node.argument.literal; typeOnly = true; }
      if (literal && ts.isStringLiteralLike(literal)) {
        const full = resolve(literal.text, file);
        if (full) {
          const target = relative(full), line = section.line + tree.getLineAndCharacterOfPosition(literal.getStart(tree)).line + 1;
          const violation = rule(source, target, typeOnly);
          edges.push({ source, target, line, typeOnly });
          if (violation) errors.push({ rule: violation, source, target, line });
        }
      }
      ts.forEachChild(node, visit);
    }
    visit(tree);
  }
}
for (const input of inputs) if (hash(fs.readFileSync(path.join(root, input.path))) !== input.sha256) throw new Error('Frontend source changed');
const report = { status: errors.length ? 'FAIL' : 'PASS', scope: 'TypeScript and Vue AST import boundaries', files: files.length, inputs, edges, errors };
if (args.includes('--output')) {
  const output = path.resolve(value('--output'));
  if (output === root || output.startsWith(root + path.sep)) throw new Error('Report must be outside source');
  fs.mkdirSync(path.dirname(output), { recursive: true }); fs.writeFileSync(output, JSON.stringify(report, null, 2) + '\n');
}
console.log(JSON.stringify({ status: report.status, files: report.files, edges: edges.length, errors }, null, 2));
if (errors.length) process.exitCode = 1;
