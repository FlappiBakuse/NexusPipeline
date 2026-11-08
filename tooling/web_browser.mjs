import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { pathToFileURL } from 'node:url';
import { spawnSync } from 'node:child_process';
import { prepareBrowserDependencies } from './browser_dependencies.mjs';
import { runControls } from './browser_controls.mjs';
import { runDesktopFixture } from './browser_desktop.mjs';
import { ordinaryPath, externalDirectory } from './artifact_paths.mjs';

const args = process.argv.slice(2), options = new Map();
for (let index = 0; index < args.length; index += 2) {
  if (!['--root', '--modules', '--browser', '--scenario', '--runtime', '--output', '--host-input'].includes(args[index]) ||
      !args[index + 1] || options.has(args[index])) throw new Error('Unknown, missing or repeated browser option');
  options.set(args[index], args[index + 1]);
}
const absolute = name => {
  const value = options.get(name);
  if (!value || !path.isAbsolute(value)) throw new Error('Explicit absolute ' + name + ' required');
  return ordinaryPath(value);
};
const root = absolute('--root'), modules = absolute('--modules'), browser = absolute('--browser');
const runtime = absolute('--runtime'), output = absolute('--output'), scenario = options.get('--scenario');
const scenarios = {
  controls: 'controls', 'controls-painted': 'controls',
  'host-automation': 'automation-module.mjs', 'host-source': 'source-module.mjs', 'host-installed': 'installed-module.mjs',
  'host-checkin': 'checkin-module.mjs', 'host-activities': 'activities-module.mjs', 'host-screenshot': 'screenshot-module.mjs',
  'host-plugin-list': 'plugin-list-module.mjs',
  'desktop-native': 'desktop', 'desktop-browser': 'desktop', 'desktop-failed': 'desktop'
};
if (!Object.hasOwn(scenarios, scenario)) throw new Error('Unknown browser scenario');
externalDirectory(root, runtime);
externalDirectory(root, output);
if (!fs.statSync(browser).isFile() || [runtime, output].some(directory => directory === root || directory.startsWith(root + path.sep) || root.startsWith(directory + path.sep)))
  throw new Error('Browser artifacts must be disjoint from source');
if (fs.existsSync(output) && fs.readdirSync(output).length) throw new Error('Browser output must be new or empty');
fs.mkdirSync(output, { recursive: true });
const digest = file => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
const started = performance.now();
let report = { status: 'FAIL', scenario, sourceMode: 'local-uncommitted', sourceCommit: null, sourceTree: null,
  command: process.argv, cases: [], browserSha256: digest(browser), nodeVersion: process.version };
function bindSource() {
  const source = JSON.parse(fs.readFileSync(path.join(runtime, 'source-receipt.json')));
  if (digest(source.sourceManifestPath) !== source.sourceSnapshotHash) throw new Error('Source manifest changed');
  const manifest = JSON.parse(fs.readFileSync(source.sourceManifestPath));
  if (!manifest.files?.length) throw new Error('Empty source inventory');
  for (const item of manifest.files) {
    const file = path.resolve(root, item.path);
    if (!file.startsWith(root + path.sep)) throw new Error('Source input leaves repository');
    ordinaryPath(file);
    if (fs.statSync(file).size !== item.sizeBytes || digest(file) !== item.sha256) throw new Error('Prepare current source before browser tests: ' + item.path);
  }
  return source;
}
function checkedInput(record) {
  if (!record || !path.isAbsolute(record.path) || !/^[a-f0-9]{64}$/.test(record.sha256)) throw new Error('Bound artifact required');
  ordinaryPath(record.path);
  if (digest(record.path) !== record.sha256) throw new Error('Bound artifact changed');
  return JSON.parse(fs.readFileSync(record.path));
}
try {
  if (scenarios[scenario] === 'desktop') {
    const manifest = JSON.parse(fs.readFileSync(path.join(runtime, 'desktop-bundle.json')));
    if (manifest.status !== 'PASS' || manifest.format !== 'nexus.desktop.bundle') throw new Error('Verified Electron bundle required');
    if (path.resolve(browser) !== path.join(runtime, 'NexusPipeline.Desktop.exe')) throw new Error('Desktop scenario requires the explicit bundled Electron executable');
    const verified = spawnSync('python', ['-B', path.join(root, 'tooling/desktop_archive.py'), '--root', root, '--bundle', runtime],
      { encoding: 'utf8', windowsHide: true, timeout: 30000 });
    if (verified.error || verified.status !== 0) throw new Error('Desktop bundle source verification failed: ' + (verified.stderr || verified.error));
    report.runtime = { manifestSha256: digest(path.join(runtime, 'desktop-bundle.json')), runtime: manifest.runtime };
    report = { ...report, ...runDesktopFixture({ root, runtime, output, manifest, scenario }) };
  } else {
    const source = bindSource(); report.source = source;
    fs.mkdirSync(path.join(runtime, 'cache/tmp'), { recursive: true });
    const dependencies = prepareBrowserDependencies(root, runtime); report.dependencies = dependencies;
    if (scenarios[scenario] === 'controls') {
      const { chromium } = await import(pathToFileURL(path.join(dependencies.modules, 'playwright-core/index.mjs')));
      report = { ...report, ...await runControls({ root, modules, browser, runtime, output, chromium, scenario }) };
    } else {
      const input = JSON.parse(fs.readFileSync(absolute('--host-input')));
      if (!path.isAbsolute(input.fixtureRoot || '') || !fs.statSync(input.fixtureRoot).isDirectory() ||
          !/^[A-Z][A-Z0-9_]{0,79}$/.test(input.tokenEnvironment || '') || !process.env[input.tokenEnvironment] || !input.instanceId)
        throw new Error('Host fixture root, instance and credential environment name required');
      ordinaryPath(input.fixtureRoot);
      if (input.context?.directory && (!path.isAbsolute(input.context.directory) ||
          !path.resolve(input.context.directory).startsWith(path.resolve(input.fixtureRoot) + path.sep)))
        throw new Error('Host scenario files leave the explicit fixture root');
      const candidate = checkedInput(input.productReceipt);
      const assembled = candidate.status === 'PASS_APPLICATION_ASSEMBLED' && candidate.format === 'nexus.product.assembly' && candidate.version === 1;
      if (assembled) {
        const application = path.dirname(input.productReceipt.path);
        if (!candidate.files?.length || !candidate.files.some(item => item.path === 'NexusPipeline.exe'))
          throw new Error('Complete assembled Host required');
        for (const item of candidate.files) {
          const member = path.resolve(application, item.path);
          if (!member.startsWith(application + path.sep)) throw new Error('Product member leaves application');
          ordinaryPath(member);
          if (fs.statSync(member).size !== item.sizeBytes || digest(member) !== item.sha256) throw new Error('Assembled product changed');
        }
      }
      if ((!assembled && candidate.status !== 'PASS') || candidate.source?.sourceSnapshotHash !== source.sourceSnapshotHash || !Array.isArray(input.pluginReceipts))
        throw new Error('Host product and current source are not bound');
      const plugins = input.pluginReceipts.map(record => {
        const plugin = checkedInput(record);
        if (plugin.status !== 'PASS' || plugin.sdkHostSource?.sourceSnapshotHash !== source.sourceSnapshotHash ||
            !plugin.extensionId || !plugin.artifact || digest(plugin.artifact.path) !== plugin.artifact.sha256)
          throw new Error('Plugin package is not bound to this Host source');
        ordinaryPath(plugin.artifact.path);
        return plugin.extensionId;
      });
      if (new Set(plugins).size !== plugins.length) throw new Error('Repeated plugin package input');
      const address = new URL(input.address);
      if (address.protocol !== 'http:' || !['127.0.0.1', 'localhost'].includes(address.hostname) || address.username || address.password)
        throw new Error('Explicit isolated loopback Host required');
      const response = await fetch(new URL('/control/v1/capabilities', address), { headers: { Authorization: 'Bearer ' + process.env[input.tokenEnvironment] }, signal: AbortSignal.timeout(5000) });
      if (!response.ok || (await response.json()).instanceId !== input.instanceId) throw new Error('Host fixture identity differs');
      const nativeInput = { ...input, modules: dependencies.modules, browser, output };
      const inputPath = path.join(output, 'input.json'); fs.writeFileSync(inputPath, JSON.stringify(nativeInput, null, 2) + '\n');
      const child = spawnSync(process.execPath, [path.join(root, 'tests/integration/browser', scenarios[scenario]), inputPath], {
        encoding: 'utf8', timeout: 120000, env: { ...process.env, TEMP: path.join(runtime, 'cache/tmp'), TMP: path.join(runtime, 'cache/tmp') } });
      fs.writeFileSync(path.join(output, 'native.log'), (child.stdout || '') + (child.stderr || ''));
      const resultPath = path.join(output, 'result.json');
      if (child.status !== 0 || child.error || !fs.existsSync(resultPath)) throw new Error('Host browser scenario did not complete');
      const native = JSON.parse(fs.readFileSync(resultPath));
      if (native.status !== 'PASS' || !native.checks?.length) throw new Error('Host browser report failed or had zero cases');
      report = { ...report, status: 'PASS', scope: 'Selected isolated Host browser scenario',
        cases: [{ name: scenario, checks: native.checks }], nativeReport: resultPath, productReceipt: input.productReceipt, pluginReceipts: input.pluginReceipts };
    }
    if (bindSource().sourceSnapshotHash !== source.sourceSnapshotHash) throw new Error('Source changed during browser scenario');
  }
  if (!report.cases.length) throw new Error('Zero browser cases');
} catch (error) { report.status = 'FAIL'; report.error = error.stack || String(error); process.exitCode = 1; }
report.elapsedMilliseconds = performance.now() - started;
fs.writeFileSync(path.join(output, 'receipt.json'), JSON.stringify(report, null, 2) + '\n');
console.log(JSON.stringify({ status: report.status, scenario, cases: report.cases.length, output, error: report.error }));
