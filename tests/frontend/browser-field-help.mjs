import fs from 'node:fs';
import path from 'node:path';

export async function checkFieldHelp(page, output) {
  const checks = await page.evaluate(async () => {
    const { registerElements } = await import('/apps/web/src/ui/register-elements.ts');
    const { tooltipContract } = await import('/tests/frontend/tooltip-contract.ts');
    const { fieldHelpContract } = await import('/tests/frontend/field-help-contract.ts');
    registerElements();
    await import('/apps/web/src/styles/theme.css');
    await import('/apps/web/src/ui/theme.css');
    await import('/apps/web/src/styles/painted-controls.css');
    const checks = [], check = (condition, name) => { if (!condition) throw Error(name); checks.push(name); };
    const temporary = document.createElement('section'); document.body.prepend(temporary);
    await tooltipContract(temporary, check); temporary.remove();
    const fixture = document.createElement('section'); document.body.prepend(fixture);
    window.fieldHelpFixture = await fieldHelpContract(fixture, check);
    return checks;
  });
  const input = page.locator('#field-help-input input');
  const button = page.locator('#field-help-input + nexus-tooltip button');
  const bubble = page.locator('nexus-tooltip .panel:visible');
  const assert = (condition, name) => { if (!condition) throw Error(name); checks.push(name); };
  await button.waitFor({ timeout: 3000 });
  const glyph = await button.locator('.glyph').boundingBox();
  const target = await button.boundingBox();
  assert(glyph?.width === 15 && glyph.height === 15 && target?.width === 20 && target.height === 20,
    'the compact question mark keeps the original button dimensions');
  assert(await button.locator('.glyph').evaluate(ring => {
    const rect = ring.getBoundingClientRect();
    const icon = ring.querySelector('svg').getBoundingClientRect();
    const path = ring.querySelector('path').getBBox();
    return Math.abs(rect.x + rect.width / 2 - icon.x - icon.width / 2) < 0.1 &&
      Math.abs(rect.y + rect.height / 2 - icon.y - icon.height / 2) < 0.1 &&
      Math.abs(path.x + path.width / 2 - 5) < 0.15 && Math.abs(path.y + path.height / 2 - 5) < 0.15;
  }), 'the question mark artwork is centered within the ring on both axes');
  await page.mouse.click(target.x + target.width / 2, target.y - 1);
  await bubble.waitFor();
  assert(await button.getAttribute('aria-expanded') === 'true', 'the hit area extends beyond the painted question mark');
  await page.keyboard.press('Escape');
  await bubble.waitFor({ state: 'hidden' });
  await input.hover();
  await input.focus();
  await page.waitForTimeout(500);
  assert(!await bubble.count(), 'hover and input focus keep field help collapsed');
  const before = await input.boundingBox();
  await button.focus();
  await page.keyboard.press('Enter');
  await bubble.waitFor();
  assert(await button.getAttribute('aria-expanded') === 'true', 'the question mark opens with Enter and exposes its state');
  assert(JSON.stringify(await input.boundingBox()) === JSON.stringify(before), 'opening help leaves the field geometry unchanged');
  const nameField = page.locator('nexus-text-input[aria-label="脚本名称"]');
  const nameQuestion = page.locator('label').filter({ has: nameField }).locator('nexus-tooltip button');
  const captionAligned = question => question.locator('.glyph').evaluate(ring => {
    const rect = ring.getBoundingClientRect();
    const text = ring.getRootNode().host.parentElement.firstElementChild.getBoundingClientRect();
    return Math.abs(rect.y + rect.height / 2 - text.y - text.height / 2) < 0.5;
  });
  assert(await captionAligned(nameQuestion), 'the field question mark shares the label text centerline');
  assert(await captionAligned(page.locator('#field-help-plugin nexus-tooltip button')),
    'the shared caption layout also centers question marks attached by plugins');
  await nameQuestion.click();
  await bubble.waitFor();
  const nameBox = await nameField.boundingBox(), nameBubble = await bubble.boundingBox();
  assert(nameBubble.y + nameBubble.height <= nameBox.y, 'help opens above a caption and leaves its input unobstructed');
  await page.keyboard.press('Escape');
  const edgeSelect = page.locator('#field-help-edge nexus-select');
  await page.locator('#field-help-edge nexus-tooltip button').click();
  await bubble.waitFor();
  const edgeBox = await edgeSelect.boundingBox(), edgeBubble = await bubble.boundingBox();
  assert(edgeBubble.y >= edgeBox.y + edgeBox.height && await bubble.getAttribute('data-side') === 'below',
    'near the top edge help moves below the full select without covering it');
  await edgeSelect.getByRole('combobox').click();
  await bubble.waitFor({ state: 'hidden' });
  await page.keyboard.press('Escape');
  await button.click();
  const session = await page.context().newCDPSession(page);
  const accessible = await session.send('Accessibility.getFullAXTree');
  fs.writeFileSync(path.join(output, 'help-accessibility.json'), JSON.stringify(accessible, null, 2) + '\n');
  assert(accessible.nodes.some(node => node.name?.value === 'Help account' &&
    node.description?.value?.includes('-1 表示不限制')), 'the input keeps its accessible description');
  await page.evaluate(async () => { (await import('/apps/web/src/app/locale.ts')).locale.value = 'en-US'; });
  await bubble.filter({ hasText: '-1 means unlimited' }).waitFor();
  assert(await button.evaluate(node => node === node.getRootNode().activeElement), 'translation preserves focus on the question mark');
  await page.keyboard.press('Space');
  await bubble.waitFor({ state: 'hidden' });
  await button.click();
  for (const [theme, width] of [['dark', 1280], ['light', 390]]) {
    await page.evaluate(theme => { document.body.dataset.theme = theme; }, theme);
    await page.setViewportSize({ width, height: 900 });
    const rect = await bubble.boundingBox();
    assert(rect && rect.x >= 0 && rect.x + rect.width <= width && rect.y >= 0 && rect.y + rect.height <= 900,
      'the pointed bubble fits the ' + theme + ' viewport');
    const anchor = await input.boundingBox();
    assert(anchor, 'the tooltip still has a visible field anchor');
    const edge = rect.y + rect.height <= anchor.y ? rect.y + rect.height + 3 : rect.y - 3;
    const tip = await bubble.evaluate((node, y) => {
      const bounds = node.getBoundingClientRect();
      const root = node.getRootNode();
      const hits = [];
      for (let x = Math.ceil(bounds.left) + 4; x < bounds.right - 4; x++)
        if (root.elementFromPoint(x, y) === node) hits.push(x);
      return hits.length > 1 ? hits[Math.floor(hits.length / 2)] : null;
    }, edge);
    assert(tip !== null, 'the shared bubble points beyond its edge toward the input in ' + theme);
    const png = await page.screenshot({ path: path.join(output, 'help-' + theme + '.png'), scale: 'css' });
    const mask = await bubble.evaluateHandle(node => {
      const style = document.createElement('style');
      style.textContent = '.panel::before { opacity: 0 !important }';
      node.getRootNode().append(style);
      return style;
    });
    let hidden;
    try { hidden = await page.screenshot({ scale: 'css' }); }
    finally { await mask.evaluate(style => style.remove()); await mask.dispose(); }
    const painted = await page.evaluate(async ({ visible, hidden, tip, edge }) => {
      async function pixels(encoded) {
        const bytes = Uint8Array.from(atob(encoded), character => character.charCodeAt(0));
        const bitmap = await createImageBitmap(new Blob([bytes], { type: 'image/png' }));
        const canvas = new OffscreenCanvas(bitmap.width, bitmap.height);
        const context = canvas.getContext('2d', { willReadFrequently: true });
        context.drawImage(bitmap, 0, 0);
        bitmap.close();
        return context.getImageData(tip - 16, Math.round(edge) - 1, 33, 3).data;
      }
      const [before, after] = await Promise.all([pixels(visible), pixels(hidden)]);
      let pointerPixels = 0, outsidePixels = 0;
      for (let pixel = 0; pixel < before.length / 4; pixel++) {
        const index = pixel * 4;
        const change = [0, 1, 2].reduce((sum, channel) =>
          sum + Math.abs(before[index + channel] - after[index + channel]), 0);
        if (change <= 12) continue;
        if (Math.abs(pixel % 33 - 16) <= 6) pointerPixels++;
        else outsidePixels++;
      }
      return pointerPixels >= 3 && outsidePixels === 0;
    }, { visible: png.toString('base64'), hidden: hidden.toString('base64'), tip, edge });
    assert(painted, 'the protruding pointer is painted in the ' + theme + ' screenshot');
  }
  await input.click();
  await bubble.waitFor({ state: 'hidden' });
  await page.evaluate(() => {
    const modal = document.createElement('nexus-modal'); modal.id = 'help-modal'; modal.title = 'Help modal';
    modal.append(document.querySelector('#field-help-form')); document.body.append(modal); modal.open = true;
    modal.addEventListener('nexus-close', () => { modal.open = false; });
  });
  await button.click();
  await bubble.waitFor();
  await page.keyboard.press('Escape');
  await bubble.waitFor({ state: 'hidden' });
  assert(await page.locator('#help-modal').evaluate(modal => modal.open), 'Escape closes help and leaves its modal open');
  assert(await button.evaluate(node => node === node.getRootNode().activeElement), 'Escape retains focus on the question mark');
  assert(await page.evaluate(() => window.fieldHelpFixture.unchanged()), 'help neither edits nor submits the real form');
  await page.evaluate(() => window.fieldHelpFixture.dispose());
  await page.locator('#help-modal').evaluate(modal => modal.remove());
  assert(!await page.locator('nexus-tooltip,[id^=nexus-help-]').count(), 'disposal removes help and its descriptions');
  await session.detach();
  return checks;
}
