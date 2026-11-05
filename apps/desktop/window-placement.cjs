'use strict';
const MIN_WIDTH = 1280;
const MIN_HEIGHT = 720;
const CAPTION_HEIGHT = 40;
const finite = (...values) => values.every(Number.isFinite);
function validSaved(value) {
  return value && typeof value === 'object' && !Array.isArray(value) &&
    Object.keys(value).length === 9 && value.format === 'nexus.desktop.window-state' && value.version === 1 &&
    typeof value.maximized === 'boolean' && Number.isSafeInteger(value.displayId) &&
    finite(value.x, value.y, value.businessWidth, value.businessHeight, value.scaleFactor) &&
    Math.abs(value.x) <= 1048576 && Math.abs(value.y) <= 1048576 &&
    value.businessWidth >= MIN_WIDTH && value.businessHeight >= MIN_HEIGHT &&
    value.businessWidth <= 32768 && value.businessHeight <= 32768 && value.scaleFactor > 0 && value.scaleFactor <= 8;
}
function intersection(a, b) {
  return Math.max(0, Math.min(a.x + a.width, b.x + b.width) - Math.max(a.x, b.x)) *
    Math.max(0, Math.min(a.y + a.height, b.y + b.height) - Math.max(a.y, b.y));
}
function chooseWindowPlacement({ displays, primaryDisplayId, saved, launchIntent = 'show', frameInsets = { width: 0, height: 0 } }) {
  if (launchIntent === 'background') return { kind: 'background' };
  const usable = displays.filter(d => Number.isSafeInteger(d.id) &&
    finite(d.scaleFactor, d.workArea.x, d.workArea.y, d.workArea.width, d.workArea.height, d.bounds.width, d.bounds.height) &&
    d.scaleFactor > 0 && d.workArea.width > 0 && d.workArea.height > 0 && d.bounds.width > 0 && d.bounds.height > 0);
  if (!usable.length || !finite(frameInsets.width, frameInsets.height) || frameInsets.width < 0 || frameInsets.height < 0)
    throw new Error('desktop_display_invalid');
  saved = validSaved(saved) ? saved : null;
  let display = usable.find(d => d.id === saved?.displayId);
  if (!display && saved) {
    const old = { x: saved.x, y: saved.y, width: saved.businessWidth, height: saved.businessHeight + CAPTION_HEIGHT };
    display = [...usable].sort((a, b) => intersection(old, b.workArea) - intersection(old, a.workArea))[0];
    if (!intersection(old, display.workArea)) display = undefined;
  }
  display ??= usable.find(d => d.id === primaryDisplayId) ?? usable[0];
  const area = display.workArea;
  const available = { width: area.width - frameInsets.width, height: area.height - frameInsets.height - CAPTION_HEIGHT };
  if (available.width < MIN_WIDTH || available.height < MIN_HEIGHT)
    return { kind: 'low-resolution', displayId: display.id, area: { ...area } };
  let width, height;
  let remembered = false;
  if (saved) {
    const factor = Math.min(1, available.width / saved.businessWidth, available.height / saved.businessHeight);
    const w = Math.floor(saved.businessWidth * factor), h = Math.floor(saved.businessHeight * factor);
    if (w >= MIN_WIDTH && h >= MIN_HEIGHT) { width = w; height = h; remembered = true; }
  }
  if (!remembered) {
    const edge = Math.round(Math.min(display.bounds.width, display.bounds.height) * display.scaleFactor);
    const tiers = edge >= 2160 ? [[2560, 1440], [1920, 1080], [1280, 720]] :
      edge >= 1440 ? [[1920, 1080], [1280, 720]] : [[1280, 720]];
    [width, height] = tiers.find(([w, h]) => w <= available.width && h <= available.height);
  }
  const outerWidth = width + frameInsets.width, outerHeight = height + CAPTION_HEIGHT + frameInsets.height;
  const x = remembered ? saved.x : area.x + (area.width - outerWidth) / 2;
  const y = remembered ? saved.y : area.y + (area.height - outerHeight) / 2;
  return { kind: 'show', displayId: display.id, maximized: saved?.maximized ?? false,
    bounds: { x: Math.round(Math.max(area.x, Math.min(x, area.x + area.width - outerWidth))),
      y: Math.round(Math.max(area.y, Math.min(y, area.y + area.height - outerHeight))), width: outerWidth, height: outerHeight } };
}
module.exports = { chooseWindowPlacement, validSaved, MIN_WIDTH, MIN_HEIGHT, CAPTION_HEIGHT };
