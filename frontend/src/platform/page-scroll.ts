import type { InjectionKey } from 'vue';

export const pageScrollViewportKey: InjectionKey<() => HTMLElement | null> = Symbol('page-scroll-viewport');
