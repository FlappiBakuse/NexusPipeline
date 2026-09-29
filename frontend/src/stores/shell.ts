import { defineStore } from "pinia";

export const useShellStore = defineStore("shell", {
  state: () => ({
    booted: false,
    bootError: "",
    navOpen: false,
    tokenPromptOpen: false,
    restartRequired: false,
    restartReasons: [] as string[],
    restarting: false,
    restartError: "",
    hostVersion: "",
    hostInstanceId: "",
    frontendBuildId: "",
    actualPort: 0,
    identityConnection: "connecting" as "connecting" | "online" | "offline" | "unauthorized",
    recoveryPhase: "idle" as "idle" | "requesting" | "waiting" | "timeout" | "failed" | "dirty-blocked" | "navigating",
  }),
  actions: {
    setHostIdentity(identity: { version: string; instanceId: string; frontendBuildId: string; actualPort: number }) {
      this.hostVersion = identity.version;
      this.hostInstanceId = identity.instanceId;
      this.frontendBuildId = identity.frontendBuildId;
      this.actualPort = identity.actualPort;
      this.identityConnection = "online";
    },
    markBooted() {
      this.booted = true;
      this.bootError = "";
    },
    markBootError(error: unknown) {
      this.bootError = error instanceof Error ? error.message : String(error || "加载失败");
    },
    closeNav() {
      this.navOpen = false;
    },
    /** 记录一次必须重启服务才能生效的改动；重复原因只记录一次。 */
    markRestartRequired(reason = "") {
      const key = String(reason || "").trim();
      if (key && !this.restartReasons.includes(key)) this.restartReasons.push(key);
      this.restartRequired = true;
      this.restartError = "";
    },
    clearRestartRequired() {
      this.restartRequired = false;
      this.restartReasons = [];
      this.restartError = "";
    },
    beginRestart() {
      this.restarting = true;
      this.restartError = "";
    },
    failRestart(message: unknown) {
      this.restarting = false;
      this.restartError = message instanceof Error ? message.message : String(message || "");
    },
    finishRestart() {
      this.restarting = false;
      this.restartError = "";
    },
  },
});
