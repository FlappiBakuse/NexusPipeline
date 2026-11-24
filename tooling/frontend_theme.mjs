import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const options = new Map();
for (let index = 2; index < process.argv.length; index += 2) {
  const [key, value] = process.argv.slice(index, index + 2);
  if (!['--root', '--plugins-root'].includes(key) || !value || options.has(key)) throw Error('Invalid theme option');
  options.set(key, value);
}
const root = path.resolve(options.get('--root') || path.join(path.dirname(fileURLToPath(import.meta.url)), '..'));
const files = directory => fs.readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
  const file = path.join(directory, entry.name);
  return entry.isDirectory() ? files(file) : /\.(css|vue|ts)$/.test(entry.name) ? [file] : [];
});
const text = file => fs.readFileSync(file, 'utf8').replace(/\/\*[\s\S]*?\*\//g, '');
const defined = new Set();
// Optional plugin textarea hook; its existing nested fallback uses the public surface token.
defined.add('--nx-color-surface-secondary');
for (const file of files(path.join(root, 'apps/web/src'))) {
  const source = text(file);
  for (const match of source.matchAll(/(--[\w-]+)\s*:/g)) defined.add(match[1]);
  for (const match of source.matchAll(/\.setProperty\(\s*['"](--[\w-]+)['"]/g)) defined.add(match[1]);
}
// These public optional overrides are supplied by the appearance API, rather than a CSS declaration.
for (const prefix of ['appearance', 'theme'])
  for (const color of ['accent', 'surface', 'text', 'muted', 'border', 'danger']) defined.add(`--_nexus-${prefix}-color-${color}`);
const consumers = ['AttemptLogs.vue', 'AdapterEditorPanel.vue', 'BindingTaskPlan.vue', 'TaskCards.vue', 'TaskSummary.vue',
  'SettingsPage.vue'].map(file => path.join(root, 'apps/web/src/pages', file));
if (options.has('--plugins-root')) consumers.push(path.join(path.resolve(options.get('--plugins-root')),
  'plugins/managed/frontend-ui/src/style.css'));
const errors = consumers.flatMap(file => [...text(file).matchAll(/var\(\s*(--[\w-]+)/g)]
  .filter(match => !defined.has(match[1])).map(match => ({ file, token: match[1] })));
console.log(JSON.stringify({ status: errors.length ? 'FAIL' : 'PASS', consumers: consumers.length, errors }, null, 2));
if (errors.length) process.exitCode = 1;
