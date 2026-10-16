import { reserveLocalResources } from "../tests/support/local-resources.mjs";
import { runProcess, getProcessRunnerState } from "../tests/support/process-runner.mjs";

const [command, ...args] = process.argv.slice(2);
if (!command) throw new Error("Usage: node tools/local-work.mjs <command> [arguments]; configure NEXUS_TEST_RESOURCE_ROOT first");
process.env.NEXUS_TEST_LOW_RESOURCE = "1";
const cancellation = new AbortController();
const cancel = () => cancellation.abort();
process.on("SIGINT", cancel); process.on("SIGTERM", cancel);
try {
  await reserveLocalResources({ signal: cancellation.signal, cleanup: () => getProcessRunnerState().cleanupComplete });
  process.exitCode = await runProcess(command, args, { signal: cancellation.signal });
} catch (error) {
  console.error(error.message); process.exitCode = error.exitCode || 1;
} finally {
  process.removeListener("SIGINT", cancel); process.removeListener("SIGTERM", cancel);
}
