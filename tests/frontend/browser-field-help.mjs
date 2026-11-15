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
    const checks = [], check = (condition, name) => { if (!condition) throw Error(name); checks.push(name); };
    const temporary = document.createElement('section'); document.body.prepend(temporary);
    await tooltipContract(temporary, check); temporary.remove();
    const fixture = document.createElement('section'); document.body.prepend(fixture);
    window.fieldHelpFixture = await fieldHelpContract(fixture, check);
    return checks;
  });
  const input = page.locator('#field-help-input input');
  const bubble = page.locator('nexus-tooltip .panel:visible');
  await input.hover();
  await bubble.waitFor({ state: 'visible' });
  checks.push('real pointer hover shows the shared field tooltip');
  const bounds = await bubble.boundingBox();
  await page.mouse.move(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2);
  await page.waitForTimeout(150);
  if (!await bubble.isVisible()) throw Error('Moving onto the tooltip dismissed its content');
  checks.push('moving from the input onto its tooltip keeps the text available to read');
  await page.keyboard.press('Escape');
  await bubble.waitFor({ state: 'hidden' });
  await input.focus();
  await bubble.waitFor({ state: 'visible' });
  const session = await page.context().newCDPSession(page);
  const accessible = await session.send('Accessibility.getFullAXTree');
  fs.writeFileSync(path.join(output, 'help-accessibility.json'), JSON.stringify(accessible, null, 2) + '\n');
  if (!accessible.nodes.some(node => node.name?.value === 'Help account' &&
      node.description?.value?.includes('-1 表示不限制'))) throw Error('Missing help in the actual input accessibility tree');
  checks.push('the browser accessibility tree exposes help on the focused input');
  await page.evaluate(async () => { (await import('/apps/web/src/app/locale.ts')).locale.value = 'en-US'; });
  await page.locator('nexus-tooltip .panel:visible').filter({ hasText: '-1 means unlimited' }).waitFor();
  if (!await input.evaluate(node => node === node.getRootNode().activeElement)) throw Error('Language update moved focus');
  await page.evaluate(async () => { (await import('/apps/web/src/app/locale.ts')).locale.value = 'zh-CN'; });
  checks.push('an open tooltip translates without replacing the focused input');
  for (const [theme, width] of [['dark', 1280], ['light', 420]]) {
    await page.evaluate(theme => {
      document.body.dataset.theme = theme;
      if (getComputedStyle(document.body).colorScheme !== theme) throw Error('Help theme did not apply');
    }, theme);
    await page.setViewportSize({ width, height: 900 });
    const rect = await bubble.boundingBox();
    if (!rect || rect.x < 0 || rect.x + rect.width > width || rect.y < 0 || rect.y + rect.height > 900)
      throw Error('Help is clipped in ' + theme + ' at ' + width);
    await page.screenshot({ path: path.join(output, 'help-' + theme + '.png') });
  }
  checks.push('dark and light help remains within desktop and narrow viewports');
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.keyboard.press('Escape');
  if (!await input.evaluate(node => node === node.getRootNode().activeElement)) throw Error('Tooltip moved keyboard focus');
  checks.push('Escape closes the tooltip and retains keyboard focus');
  await page.evaluate(() => {
    const modal = document.createElement('nexus-modal'); modal.id = 'help-modal'; modal.title = 'Help modal';
    modal.append(document.querySelector('#field-help-form')); document.body.append(modal); modal.open = true;
    modal.addEventListener('nexus-close', () => { modal.open = false; });
  });
  await input.focus();
  try { await bubble.waitFor({ state: 'visible', timeout: 3000 }); }
  catch (error) {
    const state = await page.evaluate(() => {
      const anchor = document.querySelector('#field-help-input');
      const tip = [...document.querySelectorAll('nexus-tooltip')].find(item => item.anchor === anchor);
      return { active: document.activeElement?.id, focused: anchor.matches(':focus-within'),
        rect: anchor.getBoundingClientRect().toJSON(), open: tip?.open, connected: tip?.isConnected,
        modal: document.querySelector('#help-modal')?.open };
    });
    throw Error('Modal field help failed: ' + JSON.stringify(state), { cause: error });
  }
  await page.keyboard.press('Escape'); await bubble.waitFor({ state: 'hidden' });
  if (!await page.locator('#help-modal').evaluate(modal => modal.open)) throw Error('Tooltip Escape also closed its parent modal');
  checks.push('Escape dismisses field help while preserving its enclosing modal');
  const unchanged = await page.evaluate(() => window.fieldHelpFixture.unchanged());
  if (!unchanged) throw Error('Help changed the real form or draft');
  await page.evaluate(() => window.fieldHelpFixture.dispose());
  await page.locator('#help-modal').evaluate(modal => modal.remove());
  if (await page.locator('nexus-tooltip').count() || await page.locator('[id^=nexus-help-]').count())
    throw Error('Field help left an owner or description after unmount');
  checks.push('unmount removes all field tooltips and descriptions without changing the draft');
  await session.detach();
  return checks;
}
