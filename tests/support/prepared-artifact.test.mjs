import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { preparedArtifact } from './prepared-artifact.mjs';

test('prepared binaries are reused only for matching input and intact bytes', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'nxp-prepared-'));
  let calls = 0;
  const build = async output => { calls++; fs.mkdirSync(output); fs.writeFileSync(path.join(output, 'host.exe'), 'binary'); return 0; };
  try {
    const first = await preparedArtifact(root, ['source-a', 'api'], build);
    const repeat = await preparedArtifact(root, ['source-a', 'api'], build);
    assert.equal(first.cacheHit, false); assert.equal(repeat.cacheHit, true); assert.equal(calls, 1);
    const changed = await preparedArtifact(root, ['source-b', 'api'], build);
    assert.notEqual(first.key, changed.key); assert.equal(calls, 2);
    fs.writeFileSync(path.join(changed.output, 'host.exe'), 'corrupt');
    await assert.rejects(preparedArtifact(root, ['source-b', 'api'], build), /changed/);
    assert.equal(calls, 2);
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});

test('failed preparation cannot qualify as a reusable binary', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'nxp-prepared-'));
  try {
    const failed = await preparedArtifact(root, ['source'], async output => {
      fs.mkdirSync(output); fs.writeFileSync(path.join(output, 'partial'), 'incomplete'); return 1;
    });
    assert.equal(failed.code, 1);
    await assert.rejects(preparedArtifact(root, ['source'], async () => { throw new Error('Must not rebuild unknown output'); }), /ENOENT/);
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});
