import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';

const args = process.argv.slice(2);
const option = name => args[args.indexOf(name) + 1];
const root = path.resolve(option('--root'));
const modules = path.resolve(option('--modules'));
const require = createRequire(path.join(modules, 'package.json'));
const ts = require('typescript');
const vue = require('@vue/compiler-sfc');
const words = name => (name.replace(/^I(?=[A-Z][a-z])/, '').replaceAll('BetterGi', 'Bettergi').replaceAll('MuMu', 'Mumu')
  .match(/[A-Z]+(?=[A-Z][a-z]|[0-9]|$)|[A-Z]?[a-z]+[0-9]*|[0-9]+/g) || []).length;

function declarations(source, filename) {
  const sections = filename.endsWith('.vue')
    ? (() => { const parsed = vue.parse(source, { filename });
      if (parsed.errors.length) throw new Error(filename + ': Vue parse failed');
      return [parsed.descriptor.script, parsed.descriptor.scriptSetup].filter(Boolean).map(item => ({ text: item.content, line: item.loc.start.line - 1 })); })()
    : [{ text: source, line: 0 }];
  const result = [];
  for (const section of sections) {
    const tree = ts.createSourceFile(filename + '.ts', section.text, ts.ScriptTarget.Latest, true, ts.ScriptKind.TS);
    if (tree.parseDiagnostics.length) throw new Error(filename + ': TypeScript parse failed');
    function visit(node) {
      const type = ts.isClassDeclaration(node) || ts.isInterfaceDeclaration(node) || ts.isTypeAliasDeclaration(node) || ts.isEnumDeclaration(node);
      const method = ts.isMethodDeclaration(node) || ts.isMethodSignature(node) || ts.isGetAccessor(node) || ts.isSetAccessor(node);
      const callable = ts.isFunctionDeclaration(node) || ts.isFunctionExpression(node)
        || ts.isVariableDeclaration(node) && node.initializer && (ts.isArrowFunction(node.initializer) || ts.isFunctionExpression(node.initializer));
      if ((type || method || callable) && node.name && ts.isIdentifier(node.name)) {
        result.push({ name: node.name.text, kind: type ? 'type' : method ? 'method' : 'function',
          line: section.line + tree.getLineAndCharacterOfPosition(node.name.getStart(tree)).line + 1 });
      }
      ts.forEachChild(node, visit);
    }
    visit(tree);
  }
  return result;
}

function selfTest() {
  const sample = declarations('class ClearName { fourWordMemberMethod() {} }\nfunction clearName() { function fourWordLocalFunction() {} }\nconst fourWordArrowFunction = () => {};', 'sample.ts');
  const failures = sample.filter(item => words(item.name) > 3).map(item => item.name);
  if (JSON.stringify(failures) !== JSON.stringify(['fourWordMemberMethod', 'fourWordLocalFunction', 'fourWordArrowFunction'])) throw new Error('Declaration coverage failed');
  const component = declarations('<template><div /></template>\n<script setup lang="ts">\nclass FourWordVueType {}\n</script>', 'sample.vue');
  if (component[0]?.name !== 'FourWordVueType' || component[0].line !== 3 || words(component[0].name) !== 4) throw new Error('Vue declaration coverage failed');
}

selfTest();
const generated = new Set(JSON.parse(fs.readFileSync(path.join(root, 'tooling/generated_sources.json'), 'utf8')).files.map(item => item.path));
const exceptionsPath = path.join(root, 'tooling/frontend-exceptions.json');
const exceptions = fs.existsSync(exceptionsPath) ? JSON.parse(fs.readFileSync(exceptionsPath, 'utf8')) : [];
const counts = new Map(exceptions.map((item, index) => [index, 0]));
const files = [], errors = [];
function walk(folder) {
  for (const entry of fs.readdirSync(folder, { withFileTypes: true })) {
    if (entry.name === '.git' || entry.name === 'node_modules') continue;
    const full = path.join(folder, entry.name);
    if (entry.isSymbolicLink()) throw new Error('Linked source: ' + full);
    if (entry.isDirectory()) walk(full);
    else if (/\.(?:ts|js|mjs|cjs|vue)$/.test(entry.name)) {
      const relative = path.relative(root, full).replaceAll('\\', '/');
      if (generated.has(relative)) continue;
      const items = declarations(fs.readFileSync(full, 'utf8'), relative);
      files.push({ file: relative, declarations: items.length });
      for (const item of items) {
        if (words(item.name) <= 3) continue;
        const index = exceptions.findIndex(value => value.file === relative && value.name === item.name && value.kind === item.kind && value.reason);
        if (index < 0) errors.push({ file: relative, ...item, words: words(item.name) });
        else counts.set(index, counts.get(index) + 1);
      }
    }
  }
}
walk(root);
exceptions.forEach((item, index) => { if (counts.get(index) !== item.count) errors.push({ exception: item, actualCount: counts.get(index) }); });
const result = { status: errors.length ? 'FAIL' : 'PASS', scope: 'TypeScript AST and Vue script declaration naming; generated set is exact',
  files: files.length, declarations: files.reduce((total, item) => total + item.declarations, 0), selfTest: 'PASS', errors };
process.stdout.write(JSON.stringify(result, null, 2) + '\n');
process.exitCode = errors.length ? 1 : 0;
