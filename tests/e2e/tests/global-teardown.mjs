import { stopService } from "./helpers.mjs";
import { stopUpdateStub } from "./update-stub.mjs";

export default async function globalTeardown() {
  await stopService();
  await stopUpdateStub();
}
