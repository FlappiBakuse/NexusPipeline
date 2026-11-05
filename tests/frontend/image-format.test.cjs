'use strict';
const assert = require('node:assert/strict');
const test = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
if (!process.env.NEXUS_FRONTEND_TYPESCRIPT) throw new Error('Pass the verified TypeScript module path');
const typescript = require(process.env.NEXUS_FRONTEND_TYPESCRIPT);
const source = fs.readFileSync(path.join(__dirname, '../../apps/web/src/control/image-format.ts'), 'utf8');
const script = typescript.transpileModule(source, { compilerOptions: { module: typescript.ModuleKind.CommonJS } });
const sandbox = { exports: {}, Uint8Array, DataView };
vm.runInNewContext(script.outputText, sandbox);
const { imageFormat } = sandbox.exports;

function png(width, height) {
  const value = Buffer.alloc(33);
  value.writeUInt32BE(0x89504e47); value.writeUInt32BE(0x0d0a1a0a, 4);
  value.writeUInt32BE(13, 8); value.writeUInt32BE(0x49484452, 12);
  value.writeUInt32BE(width, 16); value.writeUInt32BE(height, 20);
  return value;
}

function webp(chunk, length) {
  const value = Buffer.alloc(20 + length + (length % 2));
  value.write('RIFF'); value.writeUInt32LE(value.length - 8, 4); value.write('WEBP', 8);
  value.write(chunk, 12); value.writeUInt32LE(length, 16);
  return value;
}

test('PNG dimensions use the actual byte slice and strict declared MIME', () => {
  const storage = Buffer.concat([Buffer.alloc(7), png(360, 203), Buffer.alloc(9)]);
  const result = imageFormat(storage.subarray(7, 40), 'image/png; charset=binary');
  assert.equal(result.width, 360); assert.equal(result.height, 203); assert.equal(result.type, 'image/png');
  assert.throws(() => imageFormat(png(2, 3), 'image/jpeg'), /image_format_invalid/);
  assert.throws(() => imageFormat(png(2, 3), null), /image_format_invalid/);
});

test('JPEG skips bounded metadata and reads a progressive frame', () => {
  const value = Buffer.from([0xff, 0xd8, 0xff, 0xe1, 0, 4, 0, 0, 0xff, 0xc2, 0, 8, 8, 0, 203, 1, 104, 0]);
  const result = imageFormat(value, 'image/jpeg');
  assert.equal(result.width, 360); assert.equal(result.height, 203);
  value.writeUInt16BE(1000, 4);
  assert.throws(() => imageFormat(value, 'image/jpeg'), /image_format_invalid/);
});

test('WebP extended lossless and lossy headers preserve exact dimensions', () => {
  const extended = webp('VP8X', 10); extended.writeUIntLE(359, 24, 3); extended.writeUIntLE(202, 27, 3);
  const lossless = webp('VP8L', 5); lossless[20] = 0x2f; lossless.writeUInt32LE(359 | (202 << 14), 21);
  const lossy = webp('VP8 ', 10); lossy.set([0x9d, 1, 0x2a], 23);
  lossy.writeUInt16LE(360, 26); lossy.writeUInt16LE(203, 28);
  for (const value of [extended, lossless, lossy]) {
    const result = imageFormat(value, 'image/webp');
    assert.equal(result.width, 360); assert.equal(result.height, 203);
  }
  extended.writeUInt32LE(10000, 16);
  assert.throws(() => imageFormat(extended, 'image/webp'), /image_format_invalid/);
});

test('dimension limits and malformed containers reject before creating a browser object URL', () => {
  for (const [width, height] of [[0, 1], [1, 0], [8193, 1], [1, 8193]])
    assert.throws(() => imageFormat(png(width, height), 'image/png'), /image_dimensions_invalid/);
  assert.equal(imageFormat(png(8192, 8192), 'image/png').width, 8192);
  for (const value of [Buffer.alloc(0), Buffer.from('<svg/>'), png(2, 3).subarray(0, 24)])
    assert.throws(() => imageFormat(value, 'image/png'), /image_format_invalid/);
});
