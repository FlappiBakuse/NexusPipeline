import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { pathToFileURL } from 'node:url';

export async function runControls({ root, modules, browser, runtime, output, chromium, scenario }) {
  const { createServer } = await import(pathToFileURL(path.join(modules, 'vite/dist/node/index.js')));
  const { default: vue } = await import(pathToFileURL(path.join(modules, '@vitejs/plugin-vue/dist/index.mjs')));
  const assets = new Map(), errors = [], cases = [];
  const directory = path.join(runtime, 'controls-assets'); fs.mkdirSync(directory, { recursive: true });
  const html = '<!doctype html><html><head><meta charset="utf-8"><script type="importmap">' + JSON.stringify({ imports: {
    vue: '/@fs/' + path.join(modules, 'vue/dist/vue.runtime.esm-bundler.js').replaceAll('\\', '/'),
    '@nexuspipeline/extension-sdk': '/sdk/frontend/src/index.ts' } }) + '</script></head><body>' +
    '<script type="module" src="/tests/frontend/element-contract.ts"></script></body></html>';
  fs.writeFileSync(path.join(directory, 'index.html'), html);
  const server = await createServer({ configFile: false, root, plugins: [vue(), {
    name: 'fixture-inputs', transform(_code, id) {
      const file = path.resolve(id.split('?')[0]);
      if (path.isAbsolute(file) && file.startsWith(root + path.sep) && fs.existsSync(file) && fs.statSync(file).isFile())
        assets.set(file, crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex'));
    }, configureServer(server) {
      server.middlewares.use(async (request, response, next) => {
        if (request.url === '/control/v1/status') {
          response.setHeader('Content-Type', 'application/json');
          response.end(JSON.stringify({ fixture: 'native-window-fetch' }));
          return;
        }
        if (request.url !== '/') return next();
        try { response.setHeader('Content-Type', 'text/html'); response.end(await server.transformIndexHtml('/', html)); }
        catch (error) { next(error); }
      });
    }
  }], resolve: { alias: { vue: path.join(modules, 'vue/dist/vue.runtime.esm-bundler.js'),
    '@nexuspipeline/extension-sdk': path.join(root, 'sdk/frontend/src/index.ts') } },
    server: { host: '127.0.0.1', port: 0, fs: { allow: [root, modules] } }, optimizeDeps: { noDiscovery: true, include: [] } });
  let launched;
  try {
    await server.listen();
    const address = server.httpServer.address();
    launched = await chromium.launch({ executablePath: browser, headless: true });
    const page = await launched.newPage({ viewport: { width: 1280, height: 900 } }); page.setDefaultTimeout(15000);
    page.on('pageerror', error => errors.push(String(error)));
    await page.goto('http://127.0.0.1:' + address.port + '/');
    await page.waitForFunction(() => !!globalThis.nexusUiTests);
    const call = name => page.evaluate(async name => await globalThis.nexusUiTests[name](), name);
    if (scenario === 'controls-painted') {
      const result = await page.evaluate(async () => {
        const { registerElements } = await import('/apps/web/src/ui/register-elements.ts'); registerElements();
        const { paintedControls } = await import('/tests/frontend/painted-controls-contract.ts');
        const checks = [], fixture = document.createElement('main'); document.body.append(fixture);
        paintedControls(fixture, (condition, name) => { if (!condition) throw new Error(name); checks.push(name); });
        fixture.remove(); return checks;
      });
      if (!result.length) throw new Error('Zero painted control checks'); cases.push({ name: 'painted-controls', checks: result });
    } else {
      await call('structural'); cases.push({ name: 'element-owners-and-painted-controls' });
      const controls = await page.evaluate(async () => (await import('/tests/frontend/control-contract.ts')).runControlContract());
      if (!controls.checkCount || controls.checkCount !== controls.checks.length) throw new Error('Control contract report incomplete');
      cases.push({ name: 'control-transport-owners', checks: controls.checks });
      await call('prepareAccessibility');
      const session = await page.context().newCDPSession(page);
      const accessible = await session.send('Accessibility.getFullAXTree');
      fs.writeFileSync(path.join(output, 'accessibility.json'), JSON.stringify(accessible, null, 2) + '\n');
      const accessibleLabels = accessible.nodes.map(node =>
        [node.role?.value, node.name?.value, node.description?.value].filter(Boolean).join(':'));
      await page.evaluate(names => globalThis.nexusUiTests.verifyAccessibility(names), accessibleLabels);
      await call('prepareKeyboardSwitch'); await page.keyboard.press('Space'); await call('verifyKeyboardSwitch');
      await call('prepareComposition');
      await session.send('Input.imeSetComposition', { text: '测试', selectionStart: 2, selectionEnd: 2 });
      await call('verifyCompositionPending'); await session.send('Input.insertText', { text: '测试' }); await call('verifyCompositionCommitted');
      await call('prepareDialogs'); await page.keyboard.press('Escape'); await call('verifyDialogEscape');
      const result = await call('finish');
      if (!result.checkCount || result.checkCount !== result.checks.length) throw new Error('Element report incomplete');
      cases.push({ name: 'keyboard-ime-accessibility-and-modal-focus', checks: result.checks });
      await session.detach();
    }
    if (errors.length) throw new Error(errors.join('\n'));
    await page.screenshot({ path: path.join(output, 'controls.png') });
    if (!assets.size) throw new Error('Control source inputs were not recorded');
    for (const [file, sha] of assets) if (crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex') !== sha) throw new Error('Control source changed');
    return { status: 'PASS', scope: 'Real browser controls with fixture transport and clock ports', cases, browserVersion: launched.version(),
      assets: [...assets].map(([file, sha256]) => ({ path: path.relative(root, file).replaceAll('\\', '/'), sha256 })),
      htmlSha256: crypto.createHash('sha256').update(html).digest('hex') };
  } finally { await launched?.close(); await server.close(); }
}
