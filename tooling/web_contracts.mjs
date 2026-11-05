import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { pathToFileURL } from 'node:url';
import { ordinaryPath, externalDirectory } from './artifact_paths.mjs';

const options = new Map(), selected = [];
const args = process.argv.slice(2);
const entries = Object.fromEntries(['plugin-command', 'automation', 'account-summary', 'plugin-list', 'control-models', 'execution-plan', 'history-formatting']
  .map(name => [name, 'tests/web/' + name + '.ts']));
for (let index = 0; index < args.length; index += 2) {
  const [key, value] = args.slice(index, index + 2);
  if (!value || !['--root', '--modules', '--output', '--suite'].includes(key)) throw new Error('Unknown or missing contract option');
  if (key === '--suite') {
    if (!Object.hasOwn(entries, value) || selected.includes(value)) throw new Error('Unknown or repeated suite');
    selected.push(value);
  } else {
    if (options.has(key) || !path.isAbsolute(value)) throw new Error('Explicit absolute contract option required');
    options.set(key, ordinaryPath(value));
  }
}
const root = options.get('--root'), modules = options.get('--modules'), output = options.get('--output');
if (!root || !modules || !output || output === root || output.startsWith(root + path.sep))
  throw new Error('Pass disjoint absolute --root, --modules and --output');
externalDirectory(root, output);
if (fs.existsSync(output) && fs.readdirSync(output).length) throw new Error('Contract output must be new or empty');
if (!selected.length) selected.push('plugin-command');
fs.mkdirSync(output, { recursive: true });
const digest = file => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
const report = { status: 'RUNNING', scope: 'Selected reactive owner and Control contracts; no product side effects',
  sourceMode: 'local-uncommitted', sourceCommit: null, sourceTree: null, command: process.argv,
  nodeVersion: process.version, selectedSuites: selected, inputs: [], cases: [], errors: [] };
const started = performance.now();
const { build } = await import(pathToFileURL(path.join(modules, 'vite/dist/node/index.js')));
try {
  const inputs = new Map();
  for (const suite of selected) {
    const directory = path.join(output, suite); fs.mkdirSync(directory, { recursive: true });
    await build({ configFile: false, root, resolve: { alias: { vue: path.join(modules, 'vue/dist/vue.runtime.esm-bundler.js'),
      'pinyin-pro': path.join(modules, 'pinyin-pro/dist/index.mjs') } },
      plugins: [{ name: 'contract-inputs', transform(source, id) {
        const file = id.split('?')[0];
        if (path.isAbsolute(file) && fs.existsSync(file) && fs.statSync(file).isFile()) inputs.set(file, digest(file));
      } }],
      build: { ssr: path.join(root, entries[suite]), outDir: directory, emptyOutDir: false,
        rolldownOptions: { output: { entryFileNames: 'suite.mjs' } } },
      ssr: { noExternal: ['vue', '@vue/reactivity', '@vue/shared', 'pinyin-pro'] } });
    const test = await import(pathToFileURL(path.join(directory, 'suite.mjs')));
    const cases = await test.run();
    if (!Array.isArray(cases) || !cases.length || new Set(cases).size !== cases.length || cases.some(name => typeof name !== 'string' || !name))
      throw new Error('Missing, duplicate or zero cases in ' + suite);
    report.cases.push(...cases.map(name => ({ suite, name, status: 'PASS' })));
  }
  report.inputs = [...inputs].map(([file, sha256]) => ({ path: file, sha256 }));
  if (!report.inputs.length) throw new Error('Empty contract input closure');
  for (const input of report.inputs) if (digest(input.path) !== input.sha256) throw new Error('Contract input changed');
  report.status = 'PASS';
} catch (reason) { report.status = 'FAIL'; report.errors.push(reason.stack || String(reason)); process.exitCode = 1; }
report.elapsedMilliseconds = performance.now() - started;
fs.writeFileSync(path.join(output, 'receipt.json'), JSON.stringify(report, null, 2) + '\n');
console.log(JSON.stringify({ status: report.status, suites: selected, cases: report.cases.length, errors: report.errors, output }));
