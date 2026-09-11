import { defineStore } from "pinia";

export const useShellStore = defineStore("shell", {
  state: () => ({
    booted: false,
    bootError: "",
    navOpen: false,
    tokenPromptOpen: false,
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
  },
});
