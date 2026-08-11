const { defineConfig } = require("@playwright/test");

module.exports = defineConfig({
  testDir: "./tests",
  timeout: 300000,
  expect: { timeout: 15000 },
  workers: 1,
  fullyParallel: false,
  retries: 0,
  reporter: [["list"]],
  globalSetup: "./tests/global-setup.mjs",
  globalTeardown: "./tests/global-teardown.mjs",
  use: {
    baseURL: "http://127.0.0.1:58731/",
    channel: "msedge",
    headless: true,
    trace: "off",
  },
});
