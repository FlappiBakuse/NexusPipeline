import { setupRuntime, startService, waitForService } from "./helpers.mjs";
import { startUpdateStub } from "./update-stub.mjs";

export default async function globalSetup() {
  // 系统操作抑制：队列完成操作（休眠/重启/关机）一律不真正执行，服务进程继承本变量，CI 绝不真关机。
  process.env.NEXUS_SYSTEM_ACTION_DRYRUN = "1";
  // 模拟器 e2e 用 stub adb（隔离目录，宿主按 NEXUS_ADB_EXE 解析）。
  const { runtimeDir } = await import("./helpers.mjs");
  process.env.NEXUS_ADB_EXE = runtimeDir + "\\adb-stub\\adb-stub.cmd";
  process.env.NEXUS_MUMU_MANAGER_EXE = runtimeDir + "\\mumu-stub\\mumu-manager-stub.cmd";
  setupRuntime();
  // 更新源 stub（本地 http，zip 内容取自刚就绪的 runtime），服务经 NEXUS_UPDATE_URL 指向；global teardown 关闭。
  await startUpdateStub();
  process.env.NEXUS_UPDATE_URL = "http://127.0.0.1:58931/";
  startService();
  await waitForService();
}
