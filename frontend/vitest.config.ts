import path from "node:path";
import fs from "node:fs";
import { fileURLToPath } from "node:url";
import { defineConfig } from "vitest/config";
import vue from "@vitejs/plugin-vue";

const frontendRoot = path.dirname(fileURLToPath(import.meta.url));
const nestedPlugins = path.resolve(frontendRoot, "../NexusPipeline-Plugins");

export default defineConfig({
  plugins: [vue()],
  resolve: {
    alias: {
      "@": path.join(frontendRoot, "src"),
      "@platform": path.join(frontendRoot, "src", "platform"),
      "@bridge": path.join(frontendRoot, "src", "plugin-bridge"),
      "@official-plugins": process.env.NEXUS_OFFICIAL_PLUGINS_ROOT || (fs.existsSync(nestedPlugins) ? nestedPlugins : path.resolve(frontendRoot, "../../NexusPipeline-Plugins")),
    },
  },
  test: {
    environment: "jsdom",
    include: ["src/**/*.test.ts"],
  },
});
