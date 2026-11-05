/** Build and verify the actual SDK declarations using its source-bound tooling. */
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

const sdk = path.dirname(fileURLToPath(import.meta.url));
const explicit = process.env.NEXUS_SDK_SOURCE_ROOT;
if (explicit && !path.isAbsolute(explicit)) throw new Error('sdk_build_source_root_not_absolute');
const source = fs.realpathSync(explicit || path.resolve(sdk, '../..'));
const tool = path.join(source, 'tooling/sdk_frontend_surface.mjs');
const descriptor = path.join(source, 'contracts/sdk-frontend-api.json');
const original = path.join(source, 'sdk/frontend');
for (const name of ['package.json', 'package-lock.json', 'tsconfig.json', 'build.mjs']) {
  if (!fs.readFileSync(path.join(sdk, name)).equals(fs.readFileSync(path.join(original, name))))
    throw new Error('sdk_build_source_input_unbound');
}
const files = fs.readdirSync(path.join(original, 'src')).filter(name => name.endsWith('.ts')).sort();
const current = fs.readdirSync(path.join(sdk, 'src')).filter(name => name.endsWith('.ts')).sort();
if (JSON.stringify(files) !== JSON.stringify(current)) throw new Error('sdk_build_source_membership_unbound');
for (const name of files) {
  if (!fs.readFileSync(path.join(sdk, 'src', name)).equals(fs.readFileSync(path.join(original, 'src', name))))
    throw new Error('sdk_build_source_input_unbound');
}
const installed = JSON.parse(fs.readFileSync(path.join(sdk, 'node_modules/typescript/package.json')));
const lock = JSON.parse(fs.readFileSync(path.join(sdk, 'package-lock.json')));
if (installed.version !== '5.9.3' || lock.packages['node_modules/typescript'].version !== installed.version)
  throw new Error('sdk_build_compiler_unbound');
for (const args of [
  [path.join(sdk, 'node_modules/typescript/bin/tsc'), '-p', path.join(sdk, 'tsconfig.json')],
  [tool, path.join(sdk, 'node_modules/typescript/lib/typescript.js'), path.join(sdk, 'dist'), '--verify', descriptor],
]) {
  const result = spawnSync(process.execPath, args, { cwd: sdk, stdio: 'inherit', env: process.env });
  if (result.error) throw result.error;
  if (result.signal || result.status !== 0) process.exit(result.status || 1);
}
