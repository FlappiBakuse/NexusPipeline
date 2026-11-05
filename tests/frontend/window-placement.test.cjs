'use strict';
const assert = require('node:assert/strict');
const { test } = require('node:test');
const { EventEmitter } = require('node:events');
const fs = require('node:fs');
const path = require('node:path');
const { chooseWindowPlacement, validSaved } = require('../../apps/desktop/window-placement.cjs');
const { createWindowState } = require('../../apps/desktop/window-state.cjs');
const display = (id = 1, x = 0, width = 1920, height = 1080, scaleFactor = 1) => ({ id, scaleFactor,
  bounds: { x, y: 0, width, height }, workArea: { x, y: 0, width, height } });
const saved = (values = {}) => ({ format: 'nexus.desktop.window-state', version: 1, x: -1800, y: 50,
  businessWidth: 1280, businessHeight: 720, displayId: 2, scaleFactor: 1, maximized: false, ...values });

test('default placement includes the real caption and fits the work area', () => {
  const result = chooseWindowPlacement({ displays: [display()], primaryDisplayId: 1 });
  assert.equal(result.kind, 'show'); assert.equal(result.bounds.width, 1280); assert.equal(result.bounds.height, 760);
});
test('negative monitor coordinates and remembered maximization remain valid', () => {
  const result = chooseWindowPlacement({ displays: [display(), display(2, -1920)], primaryDisplayId: 1, saved: saved({ maximized: true }) });
  assert.equal(result.displayId, 2); assert.equal(result.bounds.x, -1800); assert.equal(result.maximized, true);
});
test('removed secondary display clamps the window onto the remaining display', () => {
  const result = chooseWindowPlacement({ displays: [display()], primaryDisplayId: 1, saved: saved() });
  assert.equal(result.displayId, 1); assert(result.bounds.x >= 0); assert(result.bounds.x + result.bounds.width <= 1920);
});
test('changed DPI and oversized remembered dimensions retain the minimum business area', () => {
  const result = chooseWindowPlacement({ displays: [display(2, -1600, 1600, 1000, 1.5)], primaryDisplayId: 2,
    saved: saved({ businessWidth: 2560, businessHeight: 1440 }) });
  assert.equal(result.kind, 'show'); assert(result.bounds.width >= 1280); assert(result.bounds.height >= 760);
  assert(result.bounds.width <= 1600); assert(result.bounds.height <= 1000);
});
test('insufficient work area requests the native low-resolution decision', () => {
  assert.equal(chooseWindowPlacement({ displays: [display(1, 0, 1024, 768)], primaryDisplayId: 1 }).kind, 'low-resolution');
  assert.equal(chooseWindowPlacement({ displays: [display()], primaryDisplayId: 1, launchIntent: 'background' }).kind, 'background');
});
test('unknown or malformed saved dimensions are rejected at the saved-state boundary', () => {
  for (const value of [saved({ version: 2 }), saved({ x: NaN }), saved({ scaleFactor: 0 }), saved({ businessWidth: 1 }),
    { ...saved(), unknown: true }, []]) assert(!validSaved(value));
});
test('unknown preference bytes are preserved when attaching and flushing a real state owner', context => {
  const root = fs.mkdtempSync(path.join(process.env.TEMP, 'window-state-'));
  context.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const original = '{"version":99,"keep":"原字节"}\n'; fs.writeFileSync(path.join(root, 'window-state.json'), original);
  const owner = createWindowState(root, { getDisplayMatching: () => display() }); owner.attach(window()); owner.flush(); owner.dispose();
  assert.equal(fs.readFileSync(path.join(root, 'window-state.json'), 'utf8'), original);
});
test('move/resize share one 200ms timer, flush persists normal bounds, closed releases listeners', context => {
  const root = fs.mkdtempSync(path.join(process.env.TEMP, 'window-state-'));
  context.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const originalSet = global.setTimeout, originalClear = global.clearTimeout;
  const timers = new Map(); let serial = 0;
  global.setTimeout = (callback, milliseconds) => { assert.equal(milliseconds, 200); timers.set(++serial, callback); return serial; };
  global.clearTimeout = id => { timers.delete(id); };
  try {
    const target = window(); const owner = createWindowState(root, { getDisplayMatching: () => display() }); owner.attach(target);
    for (let index = 0; index < 50; index++) { target.emit('move'); target.emit('resize'); }
    assert.equal(timers.size, 1); assert(!fs.existsSync(path.join(root, 'window-state.json')));
    owner.flush(); assert.equal(timers.size, 0);
    const actual = JSON.parse(fs.readFileSync(path.join(root, 'window-state.json'), 'utf8'));
    assert.equal(actual.businessHeight, 720); assert.equal(actual.maximized, true); assert.equal(actual.x, 100);
    target.emit('resize'); target.emit('closed'); assert.equal(timers.size, 0);
    for (const name of ['move', 'resize', 'maximize', 'unmaximize']) assert.equal(target.listenerCount(name), 0);
    owner.dispose();
    assert.equal(createWindowState(root, { getDisplayMatching: () => display() }).saved.maximized, true);
  } finally { global.setTimeout = originalSet; global.clearTimeout = originalClear; }
});
function window() {
  return Object.assign(new EventEmitter(), { isDestroyed: () => false, isVisible: () => true, isMinimized: () => false,
    isMaximized: () => true, getNormalBounds: () => ({ x: 100, y: 100, width: 1280, height: 760 }),
    getBounds: () => ({ width: 1920, height: 1080 }), getContentBounds: () => ({ width: 1920, height: 1080 }) });
}

test('maximized resize borders cannot shrink the remembered normal client area', context => {
  const root = fs.mkdtempSync(path.join(process.env.TEMP, 'window-state-'));
  context.after(() => fs.rmSync(root, { recursive: true, force: true }));
  let maximized = false;
  const target = Object.assign(new EventEmitter(), {
    isDestroyed: () => false, isVisible: () => true, isMinimized: () => false, isMaximized: () => maximized,
    getNormalBounds: () => ({ x: 10, y: 10, width: 1380, height: 820 }),
    getBounds: () => maximized ? { width: 1920, height: 1080 } : { width: 1380, height: 820 },
    getContentBounds: () => maximized ? { width: 1904, height: 1062 } : { width: 1380, height: 820 }
  });
  const owner = createWindowState(root, { getDisplayMatching: () => display() });
  owner.attach(target); owner.flush(); maximized = true; owner.flush();
  const state = owner.saved;
  const placement = chooseWindowPlacement({ displays: [display()], primaryDisplayId: 1, saved: state });
  assert.deepEqual(placement.bounds, { x: 10, y: 10, width: 1380, height: 820 });
  assert.equal(placement.maximized, true);
  owner.dispose();
});
