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
    const icon = mount.querySelector<HTMLImageElement>('img')!;
    await icon.decode();
    check(icon.naturalWidth > 0 && icon.getBoundingClientRect().width > 0 &&
      mount.querySelector('h1')?.textContent === 'NexusPipeline',
      'Web preparation displays a decoded brand image and the product name');
    const progress = () => mount.querySelector('progress')!;
    check(!progress().hasAttribute('value'), 'unknown preparation count uses indeterminate progress');
    total.value = 5; count.value = 2; await nextTick();
    check(progress().max === 5 && progress().value === 2 && mount.querySelector('[role="status"]')!.textContent!.includes('2/5'),
      'preparation has one live stage label and accurate bounded progress');
    const elapsed = () => Number(mount.querySelector('.startup-elapsed')!.textContent!.match(/\d+/)?.[0]);
    const started = elapsed();
    await new Promise(resolve => setTimeout(resolve, 1100));
    const advanced = elapsed();
    check(advanced > started, 'Web preparation elapsed time advances independently of progress messages');
    count.value = 4; await nextTick();
    check(elapsed() >= advanced, 'stage updates retain the elapsed preparation time');
  } finally { app.unmount(); mount.remove(); }
}
