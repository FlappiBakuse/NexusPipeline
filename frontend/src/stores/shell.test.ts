import { createPinia, setActivePinia } from "pinia";
import { beforeEach, describe, expect, it } from "vitest";
import { useShellStore } from "./shell";

describe("shell restart state", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
  });

  it("starts without a pending restart", () => {
    const shell = useShellStore();

    expect(shell.restartRequired).toBe(false);
    expect(shell.restartReasons).toEqual([]);
    expect(shell.restarting).toBe(false);
    expect(shell.restartError).toBe("");
  });

  it("records reasons without duplicates and clears them on request", () => {
    const shell = useShellStore();

    shell.markRestartRequired("plugin.install");
    shell.markRestartRequired("plugin.install");
    shell.markRestartRequired("plugin.uninstall");

    expect(shell.restartRequired).toBe(true);
    expect(shell.restartReasons).toEqual(["plugin.install", "plugin.uninstall"]);

    shell.clearRestartRequired();

    expect(shell.restartRequired).toBe(false);
    expect(shell.restartReasons).toEqual([]);
    expect(shell.restartError).toBe("");
  });

  it("tracks the restart lifecycle and clears previous errors", () => {
    const shell = useShellStore();
    shell.markRestartRequired("settings");
    shell.failRestart(new Error("探测超时"));
    expect(shell.restartRequired).toBe(true);
    expect(shell.restarting).toBe(false);
    expect(shell.restartError).toBe("探测超时");

    shell.beginRestart();
    expect(shell.restarting).toBe(true);
    expect(shell.restartError).toBe("");

    shell.failRestart("服务仍在启动");
    expect(shell.restarting).toBe(false);
    expect(shell.restartError).toBe("服务仍在启动");

    shell.beginRestart();
    shell.finishRestart();
    expect(shell.restarting).toBe(false);
    expect(shell.restartError).toBe("");
    expect(shell.restartRequired).toBe(true);
  });
});
