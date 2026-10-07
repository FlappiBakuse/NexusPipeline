import { CONTROL_SERVICE_NAME } from "./service-identity";
import { afterEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { useShellStore } from "../stores/shell";
import {
  beginServiceRecovery,
  registerRecoveryDirtyGuard,
  resumeServiceRecovery,
  startServiceObserver,
} from "./service-recovery";

const handoffId = "a".repeat(32);
const oldInstance = "b".repeat(32);
const newInstance = "c".repeat(32);
let stop: (() => void) | null = null;
let releaseGuard: (() => void) | null = null;

function identity(instanceId: string, ready = true) {
  return {
    service: CONTROL_SERVICE_NAME,
    controlApiVersion: "1",
    instanceId,
    restartHandoffId: instanceId === newInstance ? handoffId : "",
    actualPort: 58001,
    version: "0.16.12",
    frontendBuildId: instanceId === newInstance ? "build-new" : "build-old",
    ready,
  };
}

function setup(candidateReady = true) {
  setActivePinia(createPinia());
  const reload = vi.fn();
  const replace = vi.fn();
  vi.stubGlobal("location", {
    href: "http://127.0.0.1:58001/#/settings",
    origin: "http://127.0.0.1:58001",
    hostname: "127.0.0.1",
    reload,
    replace,
  });
  const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
    const url = String(input);
    if (url === "/api/status?view=identity") return new Response(JSON.stringify(identity(oldInstance)), { status: 200 });
    if (url === "/api/settings/restart") return new Response(JSON.stringify({
      handoffId, instanceId: oldInstance, newPort: 58001,
    }), { status: 200 });
    if (url === "http://127.0.0.1:58001/api/status?view=identity")
      return candidateReady
        ? new Response(JSON.stringify(identity(newInstance)), { status: 200 })
        : new Response("{}", { status: 503 });
    return new Response("{}", { status: 404 });
  });
  vi.stubGlobal("fetch", fetchMock);
  const shell = useShellStore();
  stop = startServiceObserver(shell);
  return { shell, reload, replace, fetchMock, setCandidateReady: (value: boolean) => { candidateReady = value; } };
}

afterEach(() => {
  releaseGuard?.();
  releaseGuard = null;
  stop?.();
  stop = null;
  sessionStorage.clear();
  vi.unstubAllGlobals();
});

describe("application service recovery", () => {
  it("keeps the restart handoff across the shell and reloads the same tab once", async () => {
    const { shell, reload, replace } = setup();
    await vi.waitFor(() => expect(shell.hostInstanceId).toBe(oldInstance));
    await beginServiceRecovery();
    expect(shell.hostVersion).toBe("0.16.12");
    expect(shell.hostInstanceId).toBe(newInstance);
    expect(reload).toHaveBeenCalledTimes(1);
    expect(replace).not.toHaveBeenCalled();
  });

  it("keeps unsaved inputs until the user explicitly chooses to refresh", async () => {
    const { shell, reload, fetchMock } = setup();
    releaseGuard = registerRecoveryDirtyGuard(() => true);
    await vi.waitFor(() => expect(shell.hostInstanceId).toBe(oldInstance));
    await beginServiceRecovery();
    expect(shell.recoveryPhase).toBe("dirty-blocked");
    expect(reload).not.toHaveBeenCalled();
    expect(fetchMock.mock.calls.filter(([input]) => String(input) === "/api/settings/restart")).toHaveLength(0);
    resumeServiceRecovery(true);
    await vi.waitFor(() => expect(reload).toHaveBeenCalledTimes(1));
  });

  it("reuses the pending handoff after a timeout without another restart request", async () => {
    sessionStorage.setItem("nxp-service-recovery-v1", JSON.stringify({
      schema: 1, origin: "http://127.0.0.1:58001", expiresAt: Date.now() + 300,
      handoff: { handoffId, previousInstanceId: oldInstance, newPort: 58001 },
    }));
    const { shell, reload, fetchMock, setCandidateReady } = setup(false);
    await vi.waitFor(() => expect(shell.recoveryPhase).toBe("timeout"), { timeout: 3000 });
    setCandidateReady(true);
    resumeServiceRecovery();
    await vi.waitFor(() => expect(reload).toHaveBeenCalledTimes(1));
    expect(fetchMock.mock.calls.filter(([input]) => String(input) === "/api/settings/restart")).toHaveLength(0);
  });

  it("reports a rejected restart request without probing candidate ports", async () => {
    const { shell, fetchMock } = setup();
    await vi.waitFor(() => expect(shell.hostInstanceId).toBe(oldInstance));
    fetchMock.mockImplementation(async (input: RequestInfo | URL) =>
      new Response("{}", { status: String(input) === "/api/settings/restart" ? 409 : 200 }));
    await beginServiceRecovery();
    expect(shell.recoveryPhase).toBe("failed");
    expect(fetchMock.mock.calls.some(([input]) => String(input).startsWith("http://127.0.0.1:58001/api/status"))).toBe(false);
  });
});
