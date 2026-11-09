const assert = require('node:assert/strict');
const test = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const ts = require(process.env.NEXUS_FRONTEND_TYPESCRIPT);
const source = fs.readFileSync(path.join(__dirname, '../../apps/web/src/control/image-decode.ts'), 'utf8');
const script = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS } });
function fixture(options = {}) {
  const counts = { frames: 0, codecs: 0, draws: 0, fallback: 0 };
  const frame = { pixels: 'decoded-image', close() { counts.frames++; } };
  class Codec {
    static async isTypeSupported() { return options.supported !== false; }
    constructor(init) {
      assert.equal(init.preferAnimation, false);
      assert.equal(init.type, 'image/png');
      assert.equal(Buffer.from(init.data).toString(), 'encoded');
    }
    async decode() { if (options.decode) return options.decode(frame); return { image: frame }; }
    close() { counts.codecs++; }
  }
  const context = { drawImage(image, x, y, width, height) {
    assert.equal(image.pixels, 'decoded-image'); assert.deepEqual([x, y, width, height], [0, 0, 160, 90]); counts.draws++;
    if (options.drawFailure) throw Error('paint_failed');
  } };
  const surface = { width: 0, height: 0, getContext() { return context; } };
  const sandbox = { exports: {}, ImageDecoder: options.missing ? undefined : Codec,
    async createImageBitmap() { counts.fallback++; return frame; } };
  vm.runInNewContext(script.outputText, sandbox);
  return { draw: sandbox.exports.drawImage, metrics: sandbox.exports.decodeMetrics, counts, surface, blob: new Blob(['encoded'], { type: 'image/png' }) };
}

test('decoded frames and their codecs are released after painting and after paint failure', async () => {
  for (const drawFailure of [false, true]) {
    const f = fixture({ drawFailure });
    const result = f.draw(f.blob, f.surface, 160, 90, new AbortController().signal);
    if (drawFailure) await assert.rejects(result, /paint_failed/); else await result;
    assert.equal(f.surface.width, 160); assert.equal(f.surface.height, 90);
    assert.equal(f.counts.frames, 1); assert.equal(f.counts.codecs, 1); assert.equal(f.counts.draws, 1);
  }
});
test('cancellation closes the pending decoder and closes a late frame without painting', async () => {
  let release;
  const f = fixture({ decode: frame => new Promise(resolve => { release = () => resolve({ image: frame }); }) });
  const owner = new AbortController();
  const result = f.draw(f.blob, f.surface, 160, 90, owner.signal);
  while (!release) await new Promise(resolve => setImmediate(resolve));
  owner.abort(); assert.equal(f.counts.codecs, 1); release();
  await assert.rejects(result, { name: 'AbortError' });
  assert.equal(f.counts.frames, 1); assert.equal(f.counts.draws, 0); assert.equal(f.surface.width, 0);
});
test('decode rejection releases its codec and unsupported browsers close fallback bitmaps', async () => {
  const failed = fixture({ decode: async () => { throw Error('invalid_image'); } });
  await assert.rejects(failed.draw(failed.blob, failed.surface, 160, 90, new AbortController().signal), /invalid_image/);
  assert.equal(failed.counts.codecs, 1); assert.equal(failed.counts.draws, 0);
  for (const options of [{ supported: false }, { missing: true }]) {
    const f = fixture(options); await f.draw(f.blob, f.surface, 160, 90, new AbortController().signal);
    assert.equal(f.counts.frames, 1); assert.equal(f.counts.fallback, 1); assert.equal(f.counts.codecs, 0);
  }
});

test('decode concurrency is bounded and a canceled waiter never creates a decoder', async () => {
  const pending = [];
  const f = fixture({ decode: frame => new Promise(resolve => pending.push(() => resolve({ image: frame }))) });
  const controllers = Array.from({ length: 4 }, () => new AbortController());
  const work = controllers.map(controller => f.draw(f.blob, { ...f.surface }, 160, 90, controller.signal));
  const rejected = assert.rejects(work[2], { name: 'AbortError' });
  while (pending.length < 2) await new Promise(resolve => setImmediate(resolve));
  assert.equal(f.metrics().active, 2); assert.equal(f.metrics().queued, 2);
  controllers[2].abort(); await rejected;
  assert.equal(f.metrics().queued, 1); assert.equal(pending.length, 2);
  pending[0](); pending[1]();
  while (pending.length < 3) await new Promise(resolve => setImmediate(resolve));
  pending[2](); await Promise.all([work[0], work[1], work[3]]);
  assert.equal(f.metrics().active, 0); assert.equal(f.metrics().estimatedBytes, 0); assert.equal(f.metrics().queued, 0);
  assert.equal(f.counts.frames, 3); assert.equal(f.counts.codecs, 3);
});
