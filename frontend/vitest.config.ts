import path from "node:path";
import { fileURLToPath } from "node:url";
import { defineConfig } from "vitest/config";
import vue from "@vitejs/plugin-vue";

const frontendRoot = path.dirname(fileURLToPath(import.meta.url));

export default defineConfig({
  plugins: [vue()],
  resolve: {
    alias: {
      "@": path.join(frontendRoot, "src"),
      "@platform": path.join(frontendRoot, "src", "platform"),
      "@bridge": path.join(frontendRoot, "src", "plugin-bridge"),
    },
  },
  test: {
    environment: "jsdom",
  },
});
