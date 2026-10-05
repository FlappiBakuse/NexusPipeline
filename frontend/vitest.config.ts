import path from "node:path";
import { fileURLToPath } from "node:url";
import { defineConfig } from "vitest/config";
import vue from "@vitejs/plugin-vue";
import { installationGeneration } from "../tools/installation-generation.mjs";

const frontendRoot = path.dirname(fileURLToPath(import.meta.url));

export default defineConfig({
  define: { __NEXUS_INSTALLATION_GENERATION__: JSON.stringify(installationGeneration) },
  plugins: [vue()],
  resolve: {
    alias: {
      "@": path.join(frontendRoot, "src"),
      "@platform": path.join(frontendRoot, "src", "platform"),
      "@bridge": path.join(frontendRoot, "src", "plugin-bridge"),
      ...(process.env.NEXUS_OFFICIAL_PLUGINS_ROOT ? { "@official-plugins": path.resolve(process.env.NEXUS_OFFICIAL_PLUGINS_ROOT) } : {}),
    },
  },
  test: {
    environment: "jsdom",
    include: ["src/**/*.test.ts"],
  },
});
