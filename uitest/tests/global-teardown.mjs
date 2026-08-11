import { stopService } from "./helpers.mjs";

export default async function globalTeardown() {
  await stopService();
}
