'use strict';

function clickLoginEntry(actions, expectedOrigin, expectedPath, expectedSearch, loadedAt, expiresAt) {
  if (location.origin !== expectedOrigin || location.pathname !== expectedPath ||
      location.search !== expectedSearch ||
      performance.timeOrigin > loadedAt || Date.now() >= expiresAt) return false;
  const visible = element => element.getClientRects().length > 0 && getComputedStyle(element).visibility !== 'hidden';
  const elements = selector => [...document.querySelectorAll(selector)].filter(visible);
  const loginText = /登录|登錄|log\s*in|sign\s*in/i;
  function matches(element, rule) {
    switch (rule.kind) {
      case 'visible': return true;
      case 'login-text': return loginText.test(element.textContent);
      case 'other-text': return Boolean(element.textContent.trim()) && !loginText.test(element.textContent);
      case 'account-link': return /^[0-9]+$/.test(new URL(element.href, location.href).searchParams.get('id') ?? '');
      case 'absolute': return getComputedStyle(element).position === 'absolute';
      case 'label': return rule.labels.includes(element.textContent.trim().replace(/\s+/g, ' ').toLowerCase());
      default: return false;
    }
  }
  const selected = rule => elements(rule.selector).filter(element => matches(element, rule));
  if (actions.stopWhen.some(rule => selected(rule).length)) return false;
  const clicked = globalThis.nexusLoginEntryClicks ??= new Set();
  for (const [index, step] of actions.clicks.entries()) {
    if (clicked.has(index) || step.requiresSelector && !elements(step.requiresSelector).length ||
        step.unlessSelector && elements(step.unlessSelector).length) continue;
    const targets = selected(step.target).filter(element => element instanceof HTMLElement);
    if (!targets.length || step.unique && targets.length !== 1) continue;
    clicked.add(index);
    targets[0].click();
    return index !== 0;
  }
  return true;
}

function createLoginPageActions(contents, policy, active) {
  const start = new URL(policy.startUrl);
  const actions = policy.pageActions;
  let generation = 0; let started = false; let timer; let pending;
  const matches = url => {
    try {
      const page = new URL(url);
      return !page.username && !page.password && page.origin === start.origin &&
        page.pathname === start.pathname && page.search === start.search;
    }
    catch { return false; }
  };
  function stop() { generation++; clearTimeout(timer); return pending; }
  function loaded() {
    if (started || !active() || !actions.clicks.length || contents.isLoadingMainFrame() || !matches(contents.getURL())) return;
    started = true;
    const current = generation; const frame = contents.mainFrame;
    const loadedAt = Date.now(); const expiresAt = loadedAt + 30000;
    const args = [actions, start.origin, start.pathname, start.search, loadedAt, expiresAt].map(value => JSON.stringify(value)).join(',');
    const code = '(' + clickLoginEntry.toString() + ')(' + args + ')';
    function poll() {
      if (current !== generation || !active() || Date.now() >= expiresAt || contents.isDestroyed() || contents.mainFrame !== frame ||
          contents.isLoadingMainFrame() || !matches(frame.url)) return;
      const request = contents.executeJavaScriptInIsolatedWorld(1001, [{ code }]);
      pending = request;
      void request.then(waiting => {
        if (waiting === true && current === generation) timer = setTimeout(poll, 250);
      }, () => {}).finally(() => { if (pending === request) pending = undefined; });
    }
    poll();
  }
  contents.on('did-start-navigation', event => {
    if (!event.isMainFrame) return;
    if (!event.isSameDocument || !matches(event.url)) stop();
    if (!event.isSameDocument) started = false;
  });
  contents.on('did-stop-loading', loaded);
  return stop;
}
module.exports = { createLoginPageActions };
