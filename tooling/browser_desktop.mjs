import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import crypto from 'node:crypto';

export function runDesktopFixture({ root, runtime, output, manifest, scenario }) {
  const engine = path.join(output, 'engine');
  fs.mkdirSync(engine);
  for (const item of manifest.files.filter(item => item.owner === 'electron-runtime')) {
    const target = path.resolve(engine, item.path);
    if (!target.startsWith(engine + path.sep)) throw new Error('Desktop runtime member leaves engine');
    fs.mkdirSync(path.dirname(target), { recursive: true });
    try { fs.linkSync(path.join(runtime, item.path), target); }
    catch (error) {
      if (error.code !== 'EXDEV') throw error;
      fs.copyFileSync(path.join(runtime, item.path), target, fs.constants.COPYFILE_EXCL);
    }
  }
  const profile = path.join(output, 'profile');
  for (const child of ['', 'session', 'dumps']) fs.mkdirSync(path.join(profile, child), { recursive: true });
  const target = path.join(root, 'tests/frontend/desktop-runtime.cjs');
  const mode = scenario === 'desktop-native' ? 'desktop' : scenario === 'desktop-failed' ? 'failed' : 'browser';
  const bootstrap = path.join(output, 'fixture-bootstrap.cjs');
  const script = [
    "const { app } = require('electron');",
    `app.setPath('userData', ${JSON.stringify(profile)});`,
    `app.setPath('sessionData', ${JSON.stringify(path.join(profile, 'session'))});`,
    `app.setPath('crashDumps', ${JSON.stringify(path.join(profile, 'dumps'))});`,
    `process.argv = [process.execPath, ${JSON.stringify(target)}, '--output', ${JSON.stringify(output)}, '--mode', ${JSON.stringify(mode)}];`,
    `require(${JSON.stringify(target)});`
  ].join('\n') + '\n';
  fs.writeFileSync(bootstrap, script, { flag: 'wx' });
  const executable = path.join(engine, 'NexusPipeline.Desktop.exe');
  const command = [bootstrap, '--user-data-dir=' + profile];
  const temporary = path.join(output, 'cache/tmp');
  fs.mkdirSync(temporary, { recursive: true });
  const result = spawnSync(executable, command, { encoding: 'utf8', timeout: 90000,
    env: { ...process.env, TEMP: temporary, TMP: temporary } });
  fs.writeFileSync(path.join(output, 'native.log'), (result.stdout || '') + (result.stderr || ''));
  const receipts = fs.readdirSync(output).filter(name => /^receipt-.*\.json$/.test(name));
  if (result.status !== 0 || result.error || receipts.length !== 1) throw new Error('Desktop fixture did not complete: ' + JSON.stringify({ exitCode: result.status, signal: result.signal, error: result.error?.message, receipts: receipts.length }));
  const native = JSON.parse(fs.readFileSync(path.join(output, receipts[0])));
  if (native.status !== 'PASS' || !native.cases?.length) throw new Error('Desktop fixture failed or had zero cases');
  const sha = file => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
  return { ...native, scenario, nativeReport: receipts[0], fixtureLaunch: {
    executable, command, bootstrapSha256: sha(bootstrap), fixtureSha256: sha(target),
    runtimeInput: runtime, engineExecutableSha256: sha(executable)
  } };
}
