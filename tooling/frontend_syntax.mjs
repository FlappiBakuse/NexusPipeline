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
const generated = new Set(JSON.parse(fs.readFileSync(path.join(root, 'tooling/generated_sources.json'), 'utf8')).files.map(item => item.path));
let files = 0;

function check(file, relative) {
  const source = fs.readFileSync(file, 'utf8');
  let scripts = [source];
  if (relative.endsWith('.vue')) {
    const { descriptor, errors } = vue.parse(source, { filename: relative });
    if (errors.length) throw Error(relative + ': Vue parse failed');
    scripts = [descriptor.script, descriptor.scriptSetup].filter(Boolean).map(script => script.content);
  }
  for (const script of scripts) {
    const tree = ts.createSourceFile(relative + '.ts', script, ts.ScriptTarget.Latest, false, ts.ScriptKind.TS);
    if (tree.parseDiagnostics.length) throw Error(relative + ': TypeScript parse failed');
  }
}

function walk(folder) {
  for (const entry of fs.readdirSync(folder, { withFileTypes: true })) {
    if (entry.name === '.git' || entry.name === 'node_modules') continue;
    const full = path.join(folder, entry.name);
    if (entry.isSymbolicLink()) throw Error('Linked source: ' + full);
    if (entry.isDirectory()) walk(full);
    else if (/\.(?:ts|js|mjs|cjs|vue)$/.test(entry.name)) {
      const relative = path.relative(root, full).replaceAll('\\', '/');
      if (generated.has(relative)) continue;
      check(full, relative);
      files++;
    }
  }
}

walk(root);
process.stdout.write(JSON.stringify({ status: 'PASS', scope: 'TypeScript and Vue script syntax', files }, null, 2) + '\n');
