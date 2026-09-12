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
  }),
  actions: {
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
