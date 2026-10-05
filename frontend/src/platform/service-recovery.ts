import { api, isAbortError } from "./api";
import { CONTROL_SERVICE_NAME } from "./service-identity";
import {
  applyRestartNavigation,
  requestServiceRestart,
  restartTargetUrl,
  waitForRestartedService,
  type ServiceRestartHandoff,
} from "./service-restart";
import { useShellStore } from "../stores/shell";

type ShellStore = ReturnType<typeof useShellStore>;

interface HostIdentity {
  service: string;
  controlApiVersion: string;
  instanceId: string;
  restartHandoffId: string;
  actualPort: number;
  version: string;
  frontendBuildId: string;
  ready: boolean;
}

interface SavedRecovery {
  schema: 1;
  origin: string;
  expiresAt: number;
  handoff: ServiceRestartHandoff;
}

const STORAGE_KEY = "nxp-service-recovery-v1";
const HANDLED_KEY = "nxp-service-recovery-handled-v1";
const RECOVERY_MS = 120_000;
const dirtyGuards = new Set<() => boolean>();
let shell: ShellStore | null = null;
let timer: ReturnType<typeof setTimeout> | null = null;
let controller: AbortController | null = null;
let identityRequest: AbortController | null = null;
let pending: SavedRecovery | null = null;
let observed: HostIdentity | null = null;
let generation = 0;
let active = false;

function storageGet(key: string): string | null {
  try { return sessionStorage.getItem(key); } catch { return null; }
}

function storageSet(key: string, value: string | null): void {
  try {
    if (value === null) sessionStorage.removeItem(key);
    else sessionStorage.setItem(key, value);
  } catch {
    // Recovery remains active in this tab when storage is unavailable.
  }
}

function readPending(): SavedRecovery | null {
  try {
    const value = JSON.parse(storageGet(STORAGE_KEY) || "null") as SavedRecovery | null;
    if (value?.schema !== 1 || value.origin !== location.origin || value.expiresAt <= Date.now()
        || !value.handoff?.handoffId || !/^[a-f0-9]{32}$/i.test(value.handoff.handoffId)) return null;
    return value;
  } catch { return null; }
}

function validIdentity(value: HostIdentity | null): value is HostIdentity {
  return value?.service === CONTROL_SERVICE_NAME
    && Boolean(value.controlApiVersion && value.instanceId && value.version)
    && Number.isInteger(value.actualPort) && value.actualPort > 0 && value.actualPort <= 65535;
}

function identityKey(value: HostIdentity): string {
  return `${value.instanceId}:${value.frontendBuildId}`;
}

function isDirty(): boolean {
  for (const guard of dirtyGuards) {
    try { if (guard()) return true; } catch { return true; }
  }
  return false;
}

export function registerRecoveryDirtyGuard(guard: () => boolean): () => void {
  dirtyGuards.add(guard);
  return () => dirtyGuards.delete(guard);
}

function navigateToIdentity(value: HostIdentity, discardDirty = false): void {
  if (!shell || shell.recoveryPhase === "navigating") return;
  const key = identityKey(value);
  if (storageGet(HANDLED_KEY) === key) return;
  if (!discardDirty && isDirty()) {
    shell.recoveryPhase = "dirty-blocked";
    return;
  }
  storageSet(HANDLED_KEY, key);
  storageSet(STORAGE_KEY, null);
  pending = null;
  shell.recoveryPhase = "navigating";
  const current = location.href;
  applyRestartNavigation(restartTargetUrl(current, value.actualPort), current);
}

async function checkIdentity(): Promise<void> {
  if (!active || !shell || identityRequest) return;
  const request = new AbortController();
  identityRequest = request;
  const seenGeneration = generation;
  const timeout = setTimeout(() => request.abort(), 2000);
  try {
    const next = await api<HostIdentity>("GET", "/api/status?view=identity", undefined, request.signal);
    if (!active || seenGeneration !== generation || !validIdentity(next)) return;
    const previous = observed;
    observed = next;
    shell.setHostIdentity(next);
    if (!pending && previous && next.ready && identityKey(previous) !== identityKey(next)) {
      navigateToIdentity(next);
    }
  } catch (reason) {
    if (!active || request.signal.aborted) return;
    shell.identityConnection = (reason as { status?: number })?.status === 401
      || (reason as { status?: number })?.status === 403 ? "unauthorized" : "offline";
  } finally {
    clearTimeout(timeout);
    if (identityRequest === request) identityRequest = null;
    scheduleIdentityCheck();
  }
}

function scheduleIdentityCheck(): void {
  if (timer) clearTimeout(timer);
  if (!active) return;
  timer = setTimeout(() => void checkIdentity(), document.hidden ? 60_000 : 15_000);
}

function onVisibility(): void {
  if (!document.hidden) void checkIdentity();
  else scheduleIdentityCheck();
}

function onWake(): void { void checkIdentity(); }

export function startServiceObserver(store: ShellStore): () => void {
  shell = store;
  if (active) return stopServiceObserver;
  active = true;
  pending = readPending();
  void checkIdentity();
  if (pending) void continueServiceRecovery();
  window.addEventListener("focus", onWake);
  window.addEventListener("online", onWake);
  document.addEventListener("visibilitychange", onVisibility);
  return stopServiceObserver;
}

function stopServiceObserver(): void {
  active = false;
  generation += 1;
  if (timer) clearTimeout(timer);
  timer = null;
  controller?.abort();
  controller = null;
  identityRequest?.abort();
  identityRequest = null;
  window.removeEventListener("focus", onWake);
  window.removeEventListener("online", onWake);
  document.removeEventListener("visibilitychange", onVisibility);
  shell = null;
  observed = null;
}

export async function beginServiceRecovery(): Promise<void> {
  if (!shell || !active || controller) return;
  if (pending) { await continueServiceRecovery(); return; }
  const request = new AbortController();
  controller = request;
  const currentGeneration = ++generation;
  shell.beginRestart();
  shell.recoveryPhase = "requesting";
  try {
    const handoff = await requestServiceRestart(request.signal);
    if (!active || currentGeneration !== generation || !shell) return;
    if (!handoff.handoffId || !/^[a-f0-9]{32}$/i.test(handoff.handoffId)) throw new Error("Invalid restart handoff");
    pending = { schema: 1, origin: location.origin, expiresAt: Date.now() + RECOVERY_MS, handoff };
    storageSet(STORAGE_KEY, JSON.stringify(pending));
    controller = null;
    await continueServiceRecovery();
  } catch (reason) {
    if (active && currentGeneration === generation && shell && !isAbortError(reason)) {
      shell.recoveryPhase = "failed";
      shell.failRestart(reason);
      void checkIdentity();
    }
  } finally {
    if (controller === request) controller = null;
  }
}

export async function continueServiceRecovery(): Promise<void> {
  if (!shell || !active || !pending || controller) return;
  const request = new AbortController();
  controller = request;
  const currentGeneration = ++generation;
  shell.recoveryPhase = "waiting";
  shell.beginRestart();
  const handoff = pending.handoff;
  const timeoutMs = Math.max(1000, pending.expiresAt - Date.now());
  try {
    const result = await waitForRestartedService({
      href: location.href,
      handoff,
      signal: request.signal,
      timeoutMs,
    });
    if (!active || currentGeneration !== generation || !shell) return;
    if (!result) {
      shell.recoveryPhase = "timeout";
      shell.failRestart("timeout");
      return;
    }
    const identity: HostIdentity = {
      service: CONTROL_SERVICE_NAME,
      controlApiVersion: result.controlApiVersion || "",
      instanceId: result.instanceId,
      restartHandoffId: result.restartHandoffId,
      actualPort: result.actualPort,
      version: result.version || "",
      frontendBuildId: result.frontendBuildId || "",
      ready: true,
    };
    observed = identity;
    shell.setHostIdentity(identity);
    shell.finishRestart();
    navigateToIdentity(identity);
  } catch (reason) {
    if (!isAbortError(reason) && shell) {
      shell.recoveryPhase = "failed";
      shell.failRestart(reason);
    }
  } finally {
    if (controller === request) controller = null;
  }
}

export function resumeServiceRecovery(discardDirty = false): void {
  if (!shell) return;
  if (shell.recoveryPhase === "dirty-blocked" && observed?.ready) navigateToIdentity(observed, discardDirty);
  else if (pending) {
    pending.expiresAt = Date.now() + RECOVERY_MS;
    storageSet(STORAGE_KEY, JSON.stringify(pending));
    void continueServiceRecovery();
  } else void checkIdentity();
}
