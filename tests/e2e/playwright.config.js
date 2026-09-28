const path = require("node:path");
const { defineConfig } = require("@playwright/test");

// 运行产物必须落在仓库外，否则候选构建的干净工作树检查会因未跟踪文件失败。
const outputDir = process.env.NEXUS_E2E_OUTPUT_DIR
  ? path.resolve(process.env.NEXUS_E2E_OUTPUT_DIR)
  : path.join(__dirname, ".playwright-output");

module.exports = defineConfig({
  testDir: "./tests",
  testMatch: "**/*.smoke.spec.mjs",
  outputDir,
  timeout: 120000,
  expect: { timeout: 10000 },
  workers: 1,
  fullyParallel: false,
  retries: 0,
  reporter: [["list"]],
  globalSetup: "./tests/global-setup.mjs",
  globalTeardown: "./tests/global-teardown.mjs",
  use: {
    baseURL: process.env.NEXUS_E2E_BASE_URL || "http://127.0.0.1:58731/",
    channel: "msedge",
    headless: true,
    locale: "zh-CN",
    trace: process.env.CI ? "retain-on-failure" : "off",
  },
});
