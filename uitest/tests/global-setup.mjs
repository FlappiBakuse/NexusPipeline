import { setupRuntime, startService, waitForService } from "./helpers.mjs";

export default async function globalSetup() {
  // 系统操作抑制（v0.6.3+）：队列完成操作（休眠/重启/关机）一律不真正执行，服务进程继承本变量，CI 绝不真关机。
  process.env.NEXUS_SYSTEM_ACTION_DRYRUN = "1";
  setupRuntime();
  startService();
  await waitForService();
}
