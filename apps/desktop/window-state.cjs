'use strict';
const fs = require('node:fs');
const path = require('node:path');
const { strictJson } = require('./strict-json.cjs');
const { validSaved, CAPTION_HEIGHT } = require('./window-placement.cjs');

function createWindowState(profilePath, screen) {
  const file = path.join(profilePath, 'window-state.json');
  let saved = null, writable = true, timer, window, disposed = false;
  let normalInsets = { width: 0, height: 0 };
  try {
    const stat = fs.lstatSync(file);
    if (!stat.isFile() || stat.isSymbolicLink() || stat.size > 32768) throw new Error('desktop_window_state_invalid');
    const value = strictJson(fs.readFileSync(file, 'utf8'));
    if (!validSaved(value)) throw new Error('desktop_window_state_invalid');
    saved = value;
  } catch (error) { if (error.code !== 'ENOENT') writable = false; }
  function flush() {
    clearTimeout(timer); timer = undefined;
    if (!writable || disposed || !window || window.isDestroyed() || !window.isVisible() || window.isMinimized()) return;
    const normal = window.getNormalBounds();
    // Maximized resize borders do not describe the remembered normal window frame.
    if (!window.isMaximized()) rememberInsets();
    const display = screen.getDisplayMatching(normal);
    const value = { format: 'nexus.desktop.window-state', version: 1, x: normal.x, y: normal.y,
      businessWidth: normal.width - normalInsets.width, businessHeight: normal.height - normalInsets.height - CAPTION_HEIGHT,
      displayId: display.id, scaleFactor: display.scaleFactor, maximized: window.isMaximized() };
    if (!validSaved(value)) return;
    const temporary = file + '.pending';
    try {
      fs.writeFileSync(temporary, JSON.stringify(value) + '\n', { encoding: 'utf8', mode: 0o600, flag: 'wx' });
      fs.renameSync(temporary, file); saved = value;
    } catch { writable = false; }
  }
  function schedule() { clearTimeout(timer); timer = setTimeout(flush, 200); }
  function rememberInsets() {
    const content = window.getContentBounds(), outer = window.getBounds();
    normalInsets = { width: Math.max(0, outer.width - content.width), height: Math.max(0, outer.height - content.height) };
  }
  function attach(value) {
    window = value;
    if (!value.isMaximized()) rememberInsets();
    for (const name of ['move', 'resize', 'maximize', 'unmaximize']) value.on(name, schedule);
    value.once('closed', () => {
      clearTimeout(timer); timer = undefined;
      for (const name of ['move', 'resize', 'maximize', 'unmaximize']) value.removeListener(name, schedule);
      if (window === value) window = undefined;
    });
  }
  return { get saved() { return saved; }, attach, flush,
    dispose() { flush(); disposed = true; clearTimeout(timer); } };
}
module.exports = { createWindowState };
