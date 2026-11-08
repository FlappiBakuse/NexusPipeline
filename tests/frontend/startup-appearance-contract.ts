import { createApp, h, nextTick, ref } from 'vue';
import StartupPreparation from '../../apps/web/src/app/StartupPreparation.vue';

export async function startupAppearanceContract(fixture: HTMLElement, check: (value: unknown, name: string) => void): Promise<void> {
  const count = ref(0), total = ref(0);
  const mount = document.createElement('div'); fixture.append(mount);
  const app = createApp({ render: () => h(StartupPreparation, {
    stage: '加载插件界面', completed: count.value, total: total.value
  }) });
  app.mount(mount);
  try {
    await nextTick();
    const surface = mount.querySelector<HTMLElement>('.startup-surface')!;
    const icon = mount.querySelector<HTMLImageElement>('img')!;
    await icon.decode();
    check(icon.naturalWidth > 0 && icon.getBoundingClientRect().width === 64,
      'Web preparation displays the shared native startup brand at its original size');
    check(getComputedStyle(surface).backgroundColor === 'rgb(19, 31, 51)' &&
      getComputedStyle(surface.querySelector('h1')!).fontSize === '24px',
      'Web preparation uses the native startup background and heading');
    const progress = mount.querySelector('progress')!;
    check(!progress.hasAttribute('value'), 'unknown preparation count uses indeterminate progress');
    total.value = 5; count.value = 2; await nextTick();
    check(progress.max === 5 && progress.value === 2 && mount.querySelector('[role="status"]')!.textContent!.includes('2/5'),
      'preparation has one live stage label and accurate bounded progress');
    const elapsed = mount.querySelector('.startup-elapsed')!;
    await new Promise(resolve => setTimeout(resolve, 1100));
    check(/1/.test(elapsed.textContent!), 'Web preparation elapsed time advances independently of progress messages');
    count.value = 4; await nextTick();
    check(/1/.test(elapsed.textContent!), 'stage updates retain the elapsed preparation time');
  } finally { app.unmount(); mount.remove(); }
}
