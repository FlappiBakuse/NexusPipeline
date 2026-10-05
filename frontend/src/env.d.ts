/// <reference types="vite/client" />
declare const __NEXUS_INSTALLATION_GENERATION__: string;

declare module "*.vue" {
  import type { DefineComponent } from "vue";
  const component: DefineComponent<Record<string, unknown>, Record<string, unknown>, unknown>;
  export default component;
}

declare module "*.css";
