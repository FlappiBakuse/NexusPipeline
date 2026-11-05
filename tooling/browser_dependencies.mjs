import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { spawnSync } from 'node:child_process';

export function prepareBrowserDependencies(root, runtime) {
  const directory = path.join(runtime, 'browser-tools');
  const owner = path.join(directory, '.nexus-browser-tools');
  if (fs.existsSync(directory) && fs.readdirSync(directory).length && !fs.existsSync(owner))
    throw new Error('Browser dependency directory is not owned by this runner');
  fs.mkdirSync(directory, { recursive: true });
  fs.writeFileSync(owner, 'nexus.browser-tools/1\n');
  const source = path.join(root, 'tests/browser-tools');
  const files = ['package.json', 'package-lock.json'];
  const manifest = JSON.parse(fs.readFileSync(path.join(source, 'package.json')));
  const lock = JSON.parse(fs.readFileSync(path.join(source, 'package-lock.json')));
  if (lock.lockfileVersion !== 3 || JSON.stringify(lock.packages[''].dependencies) !== JSON.stringify(manifest.dependencies)) throw new Error('Browser dependency lock differs');
  const hash = crypto.createHash('sha256');
  for (const name of files) {
    const bytes = fs.readFileSync(path.join(source, name)); hash.update(bytes);
    const target = path.join(directory, name);
    if (!fs.existsSync(target) || !fs.readFileSync(target).equals(bytes)) fs.writeFileSync(target, bytes);
  }
  const identity = hash.digest('hex'), marker = path.join(directory, 'node_modules/.nexus-lock');
  if (!fs.existsSync(marker) || fs.readFileSync(marker, 'utf8') !== identity) {
    const npm = process.platform === 'win32' ? path.join(path.dirname(process.execPath), 'node_modules/npm/bin/npm-cli.js') : 'npm';
    const command = ['ci', '--ignore-scripts', '--no-audit', '--no-fund', '--cache', path.join(runtime, 'cache/npm')];
    const result = spawnSync(process.platform === 'win32' ? process.execPath : npm,
      process.platform === 'win32' ? [npm, ...command] : command, { cwd: directory, encoding: 'utf8', timeout: 120000,
        env: { ...process.env, TEMP: path.join(runtime, 'cache/tmp'), TMP: path.join(runtime, 'cache/tmp') } });
    if (result.status !== 0 || result.error) throw new Error('Browser dependency preparation failed: ' + (result.error?.message || result.stderr));
    fs.writeFileSync(marker, identity);
  }
  return { modules: path.join(directory, 'node_modules'), dependencySha256: identity };
}
