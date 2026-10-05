import { defineConfig } from "vitest/config";
import config from "./vitest.config.ts";

if (!process.env.NEXUS_OFFICIAL_PLUGINS_ROOT) throw new Error("An explicit official Plugins checkout is required");

export default defineConfig({ ...config, test: { ...config.test, include: ["contracts/**/*.test.ts"] } });
