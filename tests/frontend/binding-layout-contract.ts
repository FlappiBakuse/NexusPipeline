import { createApp, h } from 'vue';
import type { UiElement } from '@nexuspipeline/extension-sdk';
import { createStore } from '../../apps/web/src/app/store';
import { ControlClient } from '../../apps/web/src/control/client';
import type { AutomationBundle } from '../../apps/web/src/control/wire.generated';
import { AutomationEditor, automationSession } from '../../apps/web/src/pages/automation-editor';
import AutomationEditorDialog from '../../apps/web/src/pages/AutomationEditorDialog.vue';
import { settle } from './configuration-draft-fixture';
import uiTheme from '../../apps/web/src/ui/theme.css?inline';
import workspace from '../../apps/web/src/styles/workspace.css?inline';
import theme from '../../apps/web/src/styles/theme.css?inline';

export async function bindingLayoutContract(fixture: HTMLElement, check: (value: unknown, name: string) => void): Promise<void> {
  let read: AbortSignal | null | undefined;
  const store = createStore(new ControlClient(location.origin, async (url, init) => {
    if (String(url).endsWith('/configuration/bindings/binding/resources')) {
      read = init?.signal;
      return new Promise((_resolve, reject) => read?.addEventListener('abort', () => reject(read?.reason), { once: true }));
    }
    return Response.json({ code: 'fixture_not_available' }, { status: 403 });
  }));
  store.state.capabilities = { instanceId: 'binding-layout', hostVersion: '0.18.0',
    capabilities: ['automation.write', 'extensions.read', 'configuration.read', 'configuration.edit'] };
  const user = { id: 'user', name: 'User', notes: 'Original note', order: 0 };
  const scripts = [{ id: 'other-script', name: 'Other script', kind: 'generic' },
    { id: 'script', name: 'Bound script', kind: 'specialized' }];
  const binding = { id: 'binding', priority: 1, userId: 'user', scriptId: 'script', enabled: false, runDays: -1,
    maximumSuccessfulRunsPerDay: -1, notifyEnabled: false, notificationTaskIds: [] };
  const other = { ...binding, id: 'other', scriptId: 'other-script', priority: 0, enabled: true, runDays: 5 };
  const bundle = { scripts, users: [user], bindings: [other, binding] } as AutomationBundle;
  store.state.automation = { catalogRevision: 1, roots: [], revisions: [], bundle };
  const session = automationSession(store, 'users');
  const model = new AutomationEditor(store, () => 'users', session);
  const mount = document.createElement('section'); fixture.append(mount);
  const styles = document.createElement('style');
  styles.textContent = [uiTheme, theme, workspace].join('\n');
  document.head.append(styles);
  let configured = '', saves = 0;
  const app = createApp({ render: () => model.editing.value ? h(AutomationEditorDialog, { store, model, session,
    kind: 'users', visible: true, onConfigure: id => { configured = id; }, onSave: () => { saves++; },
    onClose: () => { model.editing.value = false; } }) : null });
  app.mount(mount);
  const traverse = async (action: () => void) => {
    const moved = new Promise<void>(resolve => window.addEventListener('popstate', () => resolve(), { once: true }));
    action();
    await Promise.race([moved, new Promise((_, reject) => setTimeout(() => reject(Error('Binding history did not move')), 1200))]);
    await settle();
  };
  const title = () => mount.querySelector<HTMLElement>('[aria-current=page]');
  const detail = () => mount.querySelector<HTMLElement>('.binding-detail:not([inert])');
  const entry = (id: string) => mount.querySelector<HTMLButtonElement>(`[data-item-id="${id}"] .binding-entry`);
  const bindingGrid = () => mount.querySelector<UiElement<'nexus-sortable-list'>>('.binding-grid')!;
  const cardWidthsMatch = () => {
    const [first, second] = [...bindingGrid().children].map(element => element.getBoundingClientRect());
    if (!first || !second || Math.abs(first.width - second.width) > 1) return false;
    return matchMedia('(max-width: 600px)').matches ? first.left === second.left && first.top < second.top
      : first.top === second.top && first.right < second.left;
  };
  try {
    model.edit(user); await settle();
    check(cardWidthsMatch(), 'bound scripts share two equal columns on desktop and one column on narrow screens');
    const cardHeights = () => [...bindingGrid().children].map(element => (element as HTMLElement).offsetHeight).join(',');
    const normalHeights = cardHeights();
    const editBinding = () => mount.querySelector<HTMLButtonElement>('.bindings-section > header button')!.click();
    editBinding(); await settle();
    check(cardHeights() === normalHeights, `both binding modes reserve the same action space and card height (${normalHeights} / ${cardHeights()})`);
    editBinding(); await settle();
    const modal = mount.querySelector<UiElement<'nexus-modal'>>('nexus-modal')!;
    const scroll = modal.shadowRoot!.querySelector<HTMLElement>('nexus-scroll-area')!;
    modal.style.setProperty('--nx-desktop-offset', '520px');
    const notes = modal.querySelector<UiElement<'nexus-text-area'>>('nexus-text-area')!;
    notes.dispatchEvent(new CustomEvent('nexus-change', { detail: { value: 'Draft note' } }));
    await settle();
    const trigger = entry('binding')!;
    trigger.scrollIntoView({ block: 'end' });
    const parentScroll = scroll.scrollTop;
    const url = location.href;
    trigger.click(); await settle();
    check(title()?.textContent === 'Bound script' && location.href === url && title() === document.activeElement &&
      modal.shadowRoot?.querySelector('dialog')?.getAttribute('aria-label') === 'Bound script',
      'the first binding activation opens its detail title and focuses it without changing the Host route');
    check(parentScroll > 0 && scroll.scrollTop === 0 && !notes.getClientRects().length && !!notes.closest('[inert]'),
      'the active detail starts at the top while the account fields are inaccessible');
    const label = detail()?.querySelector('header strong');
    const badges = [...detail()!.querySelectorAll<UiElement<'nexus-badge'>>(':scope > header nexus-badge')].map(element => element.label);
    check(label?.textContent === 'Bound script' && badges.join('|') === '已停用|永久运行' &&
      getComputedStyle(detail()!).borderTopWidth === '0px', 'the flat detail repeats the account binding identity and status');
    check(['任务计划', '通用', '通知', '高级'].every(text => [...detail()!.querySelectorAll('h3')].some(item => item.textContent === text)) &&
      !!read && !read.aborted, 'the saved specialized binding probes its task plan and shows all policy groups');
    const configure = detail()!.querySelector<HTMLButtonElement>('.edit-configuration')!;
    check(!configure.disabled && configure.querySelector('small')?.textContent === '启动主程序打开该脚本实例的用户配置',
      'the detail keeps the configuration action and description');
    configure.click(); check(configured === binding.id, 'configuration editing targets the selected binding');
    const days = detail()!.querySelector<UiElement<'nexus-number-input'>>('[aria-label="运行天数"]')!;
    days.dispatchEvent(new CustomEvent('nexus-change', { detail: { value: 7 } }));
    const ancestor = modal.querySelector<HTMLAnchorElement>('nav ol li a')!;
    check(ancestor?.textContent === '用户管理' && modal.querySelectorAll('nav ol li').length === 2,
      'the breadcrumb contains only user management and the selected script');
    await traverse(() => ancestor.click());
    check(!title() && read?.aborted && trigger === document.activeElement && scroll.scrollTop === parentScroll && notes.value === 'Draft note',
      'the breadcrumb stops the plan read, restores the account scroll and focus, and keeps its draft');
    await traverse(() => history.forward());
    check(title()?.textContent === 'Bound script' && days.value === 7,
      'browser Forward returns to the same live binding draft');
    days.focus();
    const raw = days.shadowRoot!.querySelector('input')!;
    raw.value = '50000';
    raw.dispatchEvent(new Event('input', { bubbles: true, composed: true }));
    await traverse(() => history.back());
    check(!title() && trigger === document.activeElement, 'browser Back returns to the initiating binding entry');
    modal.querySelector<HTMLButtonElement>('button[type=submit]')!.click(); await settle();
    check(title()?.textContent === 'Bound script' && saves === 0 && raw === days.shadowRoot?.activeElement && raw.value === '50000',
      'saving the parent reopens the binding at its retained invalid numeric edit');
    raw.value = '7';
    raw.dispatchEvent(new Event('change', { bubbles: true }));
    const maximum = detail()!.querySelector<UiElement<'nexus-number-input'>>('[aria-label="最多成功运行次数"]')!;
    maximum.focus();
    const maximumInput = maximum.shadowRoot!.querySelector('input')!;
    maximumInput.value = '0';
    maximumInput.dispatchEvent(new Event('input', { bubbles: true, composed: true }));
    await traverse(() => history.back());
    modal.querySelector<HTMLButtonElement>('button[type=submit]')!.click(); await settle();
    check(saves === 0 && maximum.shadowRoot?.activeElement === maximumInput &&
      maximum.getAttribute('aria-invalid') === 'true' && maximumInput.getAttribute('aria-invalid') === 'true',
      'the zero daily success limit reopens marked invalid as well as focused');
    maximumInput.value = '2';
    maximumInput.dispatchEvent(new Event('change', { bubbles: true }));
    await traverse(() => history.back());
    check([...modal.querySelectorAll('.binding-grid [data-item-id]')].map(element => element.getAttribute('data-item-id')).join('|') === 'other|binding' &&
      getComputedStyle(entry('other')!.closest('.binding')!).borderRadius === '10px', 'the account keeps the original binding order and card borders');
    entry('other')!.click(); await settle();
    check(title()?.textContent === 'Other script' && !detail()?.querySelector('.binding-task-plan') &&
      detail()?.querySelector('[aria-label="运行天数"]') !== days, 'a different binding opens its own policy without a generic task-plan probe');
    const back = [...modal.querySelectorAll<HTMLButtonElement>('button')].find(button => button.textContent?.trim() === '返回上级')!;
    await traverse(() => back.click());
    check(!title() && document.activeElement === entry('other') && saves === 0, 'the footer returns without submitting and restores the other entry');
    entry('binding')!.click(); await settle();
    await traverse(() => modal.shadowRoot!.querySelector<HTMLButtonElement>('.close')!.click());
    model.edit(user); await settle();
    await traverse(() => history.forward());
    check(!title() && !!entry('binding')?.getClientRects().length, 'history from a closed editor leaves a reopened editor at the account form');
    await traverse(() => history.back());
    const toggleEditing = mount.querySelector<HTMLButtonElement>('.bindings-section header > button')!;
    toggleEditing.click(); await settle();
    const editingCard = bindingGrid().querySelector('[data-item-id="binding"]')!;
    const grip = editingCard.querySelector<UiElement<'nexus-drag-handle'>>('nexus-drag-handle');
    const remove = editingCard.querySelector<UiElement<'nexus-icon-button'>>('nexus-icon-button');
    const removeButton = remove?.shadowRoot?.querySelector('button');
    check(cardWidthsMatch() && bindingGrid().disabled && grip?.disabled && editingCard.firstElementChild === grip &&
      grip.shadowRoot?.querySelector('button')?.disabled && !entry('binding'),
      'binding edit retains the card columns and shows a disabled leading drag handle');
    check(remove?.icon === 'trash' && removeButton?.getAttribute('aria-label') === '移除绑定' &&
      !!removeButton.querySelector('svg path') && !removeButton.textContent?.trim(),
      'binding edit uses the labelled shared SVG removal action');
    const otherCard = bindingGrid().querySelector<HTMLElement>('[data-item-id="other"]')!;
    const otherWidth = otherCard.offsetWidth;
    removeButton!.click(); await settle();
    check(bindingGrid().children.length === 1 && bindingGrid().firstElementChild === otherCard &&
      otherCard.offsetWidth === otherWidth && saves === 0,
      'the removal action changes only the selected binding in the draft');
  } finally { app.unmount(); mount.remove(); styles.remove(); store.dispose(); }
}
