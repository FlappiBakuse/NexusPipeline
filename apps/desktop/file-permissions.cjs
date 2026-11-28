'use strict';
const path = require('node:path');
const { text } = require('./native-text.cjs');

function installFilePermissions(window, getPage, dialog) {
  let prompting = false;
  let generation = 0;
  const decisions = new Map();
  const current = contents => !window.isDestroyed() && contents === window.webContents &&
    !contents.isDestroyed() && contents.mainFrame.url.split('#')[0] === getPage();
  window.webContents.on('did-start-navigation', (_event, _url, inPlace, mainFrame) => {
    if (mainFrame && !inPlace) { generation++; decisions.clear(); }
  });
  const validFile = details => details?.isDirectory === false && typeof details.filePath === 'string' &&
    path.isAbsolute(details.filePath) && ['writable', 'readable'].includes(details.fileAccessType);
  function decide(contents, permission, requestingOrigin, details) {
    if (prompting || permission !== 'fileSystem' || !current(window.webContents) ||
        contents !== null && contents !== window.webContents || !validFile(details)) return false;
    const page = getPage();
    const origin = new URL(page).origin;
    if (requestingOrigin !== origin && requestingOrigin !== origin + '/' ||
        details?.requestingUrl !== undefined && details.requestingUrl.split('#')[0] !== page) return false;
    const key = path.normalize(details.filePath);
    const prior = decisions.get(key);
    if (prior && prior.expires > performance.now()) return prior.allowed;
    if (details.fileAccessType !== 'writable') return false;
    prompting = true;
    const document = generation;
    let allowed = false;
    try {
      // Electron's save-picker grant bypasses permission requests; its status check is synchronous and has no WebContents.
      const result = dialog.showMessageBoxSync(window, {
        type: 'question', title: text('saveTitle'),
        message: text('saveQuestion'), detail: details.filePath,
        buttons: [text('cancel'), text('save')], defaultId: 0, cancelId: 0, noLink: true
      });
      allowed = result === 1 && document === generation && current(window.webContents);
    } catch { }
    finally { prompting = false; }
    if (document === generation) {
      if (decisions.size >= 128) decisions.delete(decisions.keys().next().value);
      decisions.set(key, { allowed, expires: performance.now() + (allowed ? 300000 : 1000) });
    }
    return allowed;
  }
  window.webContents.session.setPermissionCheckHandler(decide);
  window.webContents.session.setPermissionRequestHandler((contents, permission, answer, details) => {
    if (!current(contents) || details?.requestingUrl?.split('#')[0] !== getPage()) { answer(false); return; }
    answer(decide(contents, permission, new URL(getPage()).origin, details));
  });
}

module.exports = { installFilePermissions };
