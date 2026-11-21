import { createApp, h, nextTick } from 'vue';
import type { UiElement } from '@nexuspipeline/extension-sdk';
import HistoryPage from '../../apps/web/src/pages/HistoryPage.vue';
import PluginsPage from '../../apps/web/src/pages/PluginsPage.vue';
import FormSelect from '../../apps/web/src/components/FormSelect';
import PageActions from '../../apps/web/src/components/PageActions.vue';
import AppNavigation from '../../apps/web/src/app/AppNavigation.vue';
import { createStore } from '../../apps/web/src/app/store';
import { ControlClient } from '../../apps/web/src/control/client';
import { settle } from './configuration-draft-fixture';
import shell from '../../apps/web/src/styles/app-shell.css?inline';
import workspace from '../../apps/web/src/styles/workspace.css?inline';
import theme from '../../apps/web/src/styles/theme.css?inline';
import uiTheme from '../../apps/web/src/ui/theme.css?inline';

type Check = (value: unknown, name: string) => void;
type Area = UiElement<'nexus-scroll-area'>;
const dates = Array.from({ length: 26 }, (_, index) => ({ date: `2026-11-${String(index + 1).padStart(2, '0')}` }));
const plugins = Array.from({ length: 20 }, (_, index) => ({ extensionId: 'fixture-' + index, isInstalled: true,
  desiredEnabled: true, activationId: 'active', installedRevision: 1, kind: 'managed-code', artifactName: 'Fixture ' + index,
  packageVersion: '0.5.0', capabilities: [] }));

function fixtureClient(): ControlClient {
  return new ControlClient(location.origin, async input => {
    const route = new URL(String(input)).pathname;
    if (route.endsWith('/records/summary/days')) return Response.json({ days: dates });
    if (route.endsWith('/extensions')) return Response.json({ hasReleaseTrust: false, items: plugins });
    if (route.endsWith('/details')) {
      const id = route.split('/').at(-2)!;
      return Response.json({ extensionId: id, packageVersion: '0.5.0', installedRevision: 1, documents: {
        'README.md': '# Document title\n\nFirst paragraph.\n\n## Usage\n\n' + 'User instructions.\n\n'.repeat(24),
        'CHANGELOG.md': '# Duplicate release title\n\n## 0.5.0\n\n- Fixed.'
      }, manifest: { format: 'nexus.extension.manifest', version: 1, extensionId: id, artifactName: id, displayName: id,
        minHostVersion: '0.18.0', packageVersion: '0.5.0', kind: 'managed-code' } });
    }
    throw Error('Unexpected card workspace request: ' + route);
  });
}

export async function cardWorkspaceContract(check: Check): Promise<void> {
  const style = document.createElement('style');
  style.textContent = [theme, uiTheme, workspace, shell].join('\n');
  document.head.append(style);
  try {
    await inspectNavigation(check);
    for (const [page, list] of [[HistoryPage, '.history-index-pane'], [PluginsPage, '.plugin-list']] as const)
      await inspectWorkspace(page, list, check);
  } finally { style.remove(); }
}

async function inspectNavigation(check: Check): Promise<void> {
  const holder = document.createElement('div');
  const store = createStore(fixtureClient());
  const app = createApp({ render: () => h(AppNavigation, { store, desktopMode: false, themeBusy: false, themeIcon: 'moon',
    routes: [{ extensionId: 'game-checkin', id: 'tasks', path: '/plugin/game-checkin/tasks', label: '签到' }] }) });
  document.body.append(holder);
  app.mount(holder);
  try {
    if (innerWidth <= 820) {
      holder.querySelector<HTMLButtonElement>('.menu-toggle')!.click();
      await nextTick();
    }
    const sidebar = holder.querySelector<HTMLElement>('.sidebar')!;
    const viewport = sidebar.querySelector<Area>('.sidebar-navigation')!.shadowRoot!.querySelector<HTMLElement>('.viewport')!;
    const lastLink = sidebar.querySelector<HTMLElement>('a[href="#/settings"]')!;
    const footer = sidebar.querySelector<HTMLElement>('.sidebar-footer')!;
    viewport.scrollTop = 0;
    const chrome = sidebar.getBoundingClientRect().height - viewport.clientHeight;
    const itemHeight = Math.ceil(lastLink.getBoundingClientRect().bottom - viewport.getBoundingClientRect().top);
    const resize = (extra: number) => { sidebar.style.height = `${chrome + itemHeight + extra}px`; };
    resize(4);
    const fits = () => viewport.scrollHeight === viewport.clientHeight && viewport.scrollTop === 0;
    viewport.scrollTop = 100;
    check(fits(), 'sidebar cannot scroll when all of its navigation links fit');

    resize(-56);
    const footerTop = footer.getBoundingClientRect().top;
    viewport.scrollTop = viewport.scrollHeight;
    check(viewport.scrollTop > 0 && Math.abs(lastLink.getBoundingClientRect().bottom - viewport.getBoundingClientRect().bottom) <= 1 &&
      footer.getBoundingClientRect().top === footerTop, 'short sidebars scroll until the last link is visible and keep the footer fixed');

    resize(24);
    check(fits(), 'growing the sidebar clears the previous scroll position');
  } finally { app.unmount(); holder.remove(); store.dispose(); }
}

async function inspectWorkspace(component: typeof HistoryPage | typeof PluginsPage, list: string, check: Check) {
  const holder = document.createElement('div');
  const store = createStore(fixtureClient());
  store.state.capabilities = { instanceId: 'contained-workspace', hostVersion: '0.18.0', capabilities: ['extensions.read'] };
  holder.innerHTML = `<div class="page-shell" style="height: 580px">
    <nexus-scroll-area class="workspace-scroll" data-card-scroll="true"><main class="workspace">
      <header class="workspace-header"><div><h1>Page</h1><p><span id="page-description-override"></span></p></div>
        <div id="page-actions"></div></header><section class="app-page"></section>
    </main></nexus-scroll-area></div>`;
  document.body.append(holder);
  const app = createApp({ render: () => h(component, { store, visible: true }) });
  app.component('FormSelect', FormSelect).component('PageActions', PageActions);
  app.mount(holder.querySelector('.app-page')!);
  const outer = holder.querySelector<Area>('.workspace-scroll')!;
  function verify(area: Area, label: string): void {
    const viewport = outer.shadowRoot!.querySelector('.viewport')!;
    const inner = area.shadowRoot!.querySelector('.viewport')!;
    check(outer.scrollHeight <= viewport.clientHeight + 1 && outer.scrollTop === 0, label + ' fits inside the workspace');
    const top = holder.querySelector('.workspace-header')!.getBoundingClientRect().top;
    area.scrollTop = area.scrollHeight;
    check(area.scrollTop > 0 && outer.scrollTop === 0 && holder.querySelector('.workspace-header')!.getBoundingClientRect().top === top,
      `${label} scrolls while the page title stays still (${innerWidth}px, ${area.scrollTop}/${inner.clientHeight}/${area.scrollHeight}, outer ${outer.scrollTop})`);
  }
  try {
    await settle();
    verify(holder.querySelector<Area>(list)!, list);
    if (component === HistoryPage) return;
    if (innerWidth <= 719) {
      holder.querySelector<HTMLButtonElement>('.plugin-row')!.click();
      await nextTick();
    }
    for (let attempt = 0; attempt < 100 && holder.querySelectorAll('.markdown-document').length !== 2; attempt++)
      await new Promise(resolve => setTimeout(resolve, 10));
    const detail = holder.querySelector<Area>('.plugin-details')!;
    verify(detail, 'the plugin detail card');
    const headings = [...detail.querySelectorAll('.markdown-document :is(h1,h2)')].map(item => item.textContent);
    check(headings.join(',') === 'Usage,0.5.0', 'embedded plugin documents omit cover headings and retain section and version headings (' + headings.join(', ') + ')');
  } finally { app.unmount(); holder.remove(); store.dispose(); }
}
