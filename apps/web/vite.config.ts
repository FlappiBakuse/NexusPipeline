import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';
import { fileURLToPath } from 'node:url';

export default defineConfig({
  plugins: [vue({ template: { compilerOptions: { isCustomElement: tag => tag.startsWith('nexus-') } } })],
  resolve: { alias: { '@nexuspipeline/extension-sdk': fileURLToPath(new URL('../../sdk/frontend/src/index.ts', import.meta.url)) } },
  build: { target: 'es2022', sourcemap: false, assetsInlineLimit: 0 },
});
