import type { InjectionKey } from 'vue';
import type { FrontendRegistry } from './frontend-registry';
export const registryKey: InjectionKey<FrontendRegistry> = Symbol('frontend-registry');
