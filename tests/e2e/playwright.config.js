const { defineConfig } = require("@playwright/test");

module.exports = defineConfig({
  testDir: "./tests",
  testMatch: "**/*.smoke.spec.mjs",
  timeout: 120000,
  expect: { timeout: 10000 },
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
    trace: process.env.CI ? "retain-on-failure" : "off",
  },
});
