import { createApp, h, nextTick } from 'vue';
import AccountFields from '../../apps/web/src/pages/AccountFields.vue';
import { ControlClient } from '../../apps/web/src/control/client';
import { createStore } from '../../apps/web/src/app/store';
import type { AutomationBundle } from '../../apps/web/src/control/wire.generated';

export async function bindingLayoutContract(fixture: HTMLElement, check: (value: unknown, name: string) => void): Promise<void> {
  const store = createStore(new ControlClient(location.origin));
  const user = { id: 'user', name: 'User' } as AutomationBundle['users'][number];
  const binding = { id: 'binding', userId: 'user', scriptId: 'script', enabled: false, runDays: -1,
    maximumSuccessfulRunsPerDay: -1, notifyEnabled: false, notificationTaskIds: [] } as AutomationBundle['bindings'][number];
  const other = { ...binding, id: 'other' };
  const bindings = [other, binding];
  const bundle = { scripts: [{ id: 'script', name: 'Bound script', kind: 'generic' }], bindings } as AutomationBundle;
  const mount = document.createElement('section'); mount.style.width = '900px'; fixture.append(mount);
  let configured = '';
  const app = createApp({ render: () => h(AccountFields, { store, modelValue: user, bindings, bundle,
    existing: true, visible: true, onConfigure: id => { configured = id; } }) });
  app.mount(mount);
  try {
    await nextTick();
    const heading = () => mount.querySelector<HTMLElement>('[data-item-id="binding"] .binding-heading')!;
    const title = heading().querySelector<HTMLElement>('strong')!;
    check(!mount.querySelector('.edit-configuration') && title.textContent === 'Bound script',
      'collapsed binding exposes its name and status without the configuration action covering them');
    const box = heading().getBoundingClientRect(), text = title.getBoundingClientRect();
    check(text.width > 0 && text.left >= box.left && text.right <= box.right && box.height < 100,
      'collapsed binding title stays inside its compact heading');
    heading().querySelector<HTMLButtonElement>('.binding-toggle')!.click(); await nextTick();
    await new Promise(resolve => setTimeout(resolve, 220));
    const selected = () => heading().closest<HTMLElement>('.binding')!;
    check(selected().getBoundingClientRect().top < mount.querySelector<HTMLElement>('[data-item-id="other"]')!.getBoundingClientRect().top,
      'expanding a later binding places it above the other cards');
    check(getComputedStyle(mount.querySelector('.binding-grid')!).opacity === '1' && !mount.querySelector('.binding-grid')!.hasAttribute('aria-disabled'),
      'locking reordering keeps binding contents at full opacity and available to assistive technology');
    const configure = mount.querySelector<HTMLButtonElement>('.binding-content .edit-configuration');
    check(configure && !configure.disabled, 'expanding reveals the saved binding configuration action in its content');
    configure!.click(); check(configured === binding.id, 'configuration action targets the selected binding');
    heading().querySelector<HTMLButtonElement>('.binding-toggle')!.click(); await nextTick();
    check(selected().querySelector<HTMLElement>('.binding-reveal')!.inert, 'collapsing excludes retained configuration controls from interaction');
    await new Promise(resolve => setTimeout(resolve, 220));
    check(selected().getBoundingClientRect().left > mount.querySelector<HTMLElement>('[data-item-id="other"]')!.getBoundingClientRect().left,
      'collapsing restores the saved binding order');
  } finally { app.unmount(); mount.remove(); store.dispose(); }
}
