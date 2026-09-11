import path from "node:path";
import { fileURLToPath } from "node:url";
import { defineConfig } from "vite";
import vue from "@vitejs/plugin-vue";

const frontendRoot = path.dirname(fileURLToPath(import.meta.url));

export default defineConfig({
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
