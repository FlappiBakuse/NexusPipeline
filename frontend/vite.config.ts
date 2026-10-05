import path from "node:path";
import { fileURLToPath } from "node:url";
import { defineConfig } from "vite";
import vue from "@vitejs/plugin-vue";
import { installationGeneration } from "../tools/installation-generation.mjs";

const frontendRoot = path.dirname(fileURLToPath(import.meta.url));

export default defineConfig({
  define: { __NEXUS_INSTALLATION_GENERATION__: JSON.stringify(installationGeneration) },
  root: frontendRoot,
  plugins: [vue()],
  resolve: {
    alias: {
      "@": path.join(frontendRoot, "src"),
      "@platform": path.join(frontendRoot, "src", "platform"),
      "@bridge": path.join(frontendRoot, "src", "plugin-bridge"),
    },
  },
  server: {
    fs: {
      allow: [path.join(frontendRoot, "..")],
    },
  },
  build: {
    outDir: path.join(frontendRoot, "dist"),
    emptyOutDir: true,
    manifest: true,
  },
});
