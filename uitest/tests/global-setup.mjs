import { setupRuntime, startService, waitForService } from "./helpers.mjs";

export default async function globalSetup() {
  setupRuntime();
  startService();
  await waitForService();
}
