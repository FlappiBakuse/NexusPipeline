import { spawn as defaultSpawn, execFile } from "node:child_process";
import { quoteWindowsArg } from "./windows-command.mjs";

let runnerState = {
  pendingCleanups: 0,
  cleanupComplete: true,
  cleanupFailures: [],
};

export function resetProcessRunnerState() {
  runnerState = {
    pendingCleanups: 0,
    cleanupComplete: true,
    cleanupFailures: [],
  };
}

export function getProcessRunnerState() {
  return {
    cleanupComplete: runnerState.cleanupComplete && runnerState.pendingCleanups === 0,
    cleanupFailures: [...runnerState.cleanupFailures],
  };
}

function recordCleanupFailure(message) {
  runnerState.cleanupComplete = false;
  runnerState.cleanupFailures.push(message);
}

function positiveTimeout(value, fallback) {
  return Number.isFinite(value) && value > 0 ? value : fallback;
}

/**
 * Run one owned child process with bounded timeout cleanup. A timeout never
 * waits indefinitely for `close`; the caller receives timeoutCode once the
 * cleanup deadline is reached and the finalizer can preserve the evidence.
 */
export function runProcess(command, args, options = {}) {
  return new Promise(resolve => {
    if (options.signal?.aborted) return resolve(130);
    const available = options.budget?.remainingMs() ?? Infinity;
    if (available <= 0) return resolve(options.timeoutCode ?? 5);
    const requested = options.budget?.limitMs === null ? Infinity : positiveTimeout(options.timeoutMs, Infinity);
    const bounded = Math.min(available, requested);
    const timeoutMs = Number.isFinite(bounded) ? Math.max(1, Math.floor(bounded)) : null;
    const timeoutCode = options.timeoutCode ?? 5;
    let stopCode = timeoutCode;
    const timeoutCleanupMs = positiveTimeout(options.timeoutCleanupMs, 5_000);
    const isShim = process.platform === "win32" && /\.(?:cmd|bat)$/iu.test(command);
    const spawnCommand = isShim ? (process.env.ComSpec || "cmd.exe") : command;
    const spawnArgs = isShim
      ? ["/d", "/s", "/c", "call", quoteWindowsArg(command), ...args.map(quoteWindowsArg)]
      : args;
    const childEnv = { ...(options.env || process.env) };
    for (const key of Object.keys(childEnv)) {
      if (key.toUpperCase().startsWith("NEXUS_CI_")) delete childEnv[key];
    }
    const label = [command, ...args].join(" ");
    const startedAt = performance.now();
    const spawn = options.spawnImpl || defaultSpawn;
    const killProcessTree = options.killProcessTreeImpl || ((pid, remainingMs) => new Promise(resolve => {
      // Only the still-live ChildProcess may authorize this numeric PID.
      if (child.exitCode !== null || child.signalCode !== null) return resolve(false);
      execFile("taskkill", ["/PID", String(pid), "/T", "/F"], { windowsHide: true, timeout: Math.max(1, Math.floor(remainingMs)) }, error => resolve(!error));
    }));
    const waitForExit = options.waitForExitImpl || (async () => {
      while (!settled && child.exitCode === null && child.signalCode === null) {
        await new Promise(resolve => { const timer = setTimeout(resolve, 25); timer.unref(); });
      }
      return child.exitCode !== null || child.signalCode !== null;
    });
    let settled = false;
    let timedOut = false;
    let timeoutHandle = null;
    let cleanupHandle = null;
    let closed = false;
    let cleanupVerified = false;

    const finish = code => {
      if (settled) return;
      settled = true;
      if (timeoutHandle) clearTimeout(timeoutHandle);
      if (cleanupHandle) clearTimeout(cleanupHandle);
      options.signal?.removeEventListener("abort", onAbort);
      if (timedOut) {
        runnerState.pendingCleanups--;
        child.removeListener("close", onClose);
        child.removeListener("error", onError);
        child.on("error", () => {});
        // Preserve output already delivered, but do not let an unclosed pipe
        // or a surviving child keep this failed runner alive indefinitely.
        for (const stream of [child.stdin, child.stdout, child.stderr]) stream?.destroy?.();
        child.unref?.();
      }
      if (options.budget) {
        const timing = "[耗时] "+label+": "+Math.round(performance.now()-startedAt)+"ms; exit "+code+"\n";
        console.error(timing.trimEnd());options.onOutput?.(timing);
      }
      resolve(code);
    };

    let child;
    try {
      child = spawn(spawnCommand, spawnArgs, {
        cwd: options.cwd || options.defaultCwd || process.cwd(),
        env: childEnv,
        stdio: options.onOutput ? ["inherit", "pipe", "pipe"] : options.stdio || "inherit",
        windowsHide: true,
        windowsVerbatimArguments: isShim,
      });
    } catch (error) {
      console.error(`[错误] 启动 ${command} 失败：${error.message}`);
      finish(1);
      return;
    }

    if (options.onOutput) {
      child.stdout?.setEncoding("utf8");
      child.stderr?.setEncoding("utf8");
      child.stdout?.on("data", chunk => { process.stdout.write(chunk); options.onOutput(chunk); });
      child.stderr?.on("data", chunk => { process.stderr.write(chunk); options.onOutput(chunk); });
    }

    function onError(error) {
      console.error(`[错误] 启动 ${command} 失败：${error.message}`);
      if (!timedOut) finish(1);
    }
    function onClose(code, signal) {
      closed = true;
      if (timedOut) {
        if (cleanupVerified) finish(stopCode);
        return;
      }
      if (signal) {
        console.error(`[错误] ${command} 被信号 ${signal} 终止`);
        return finish(1);
      }
      finish(code ?? 1);
    }
    child.once("error", onError);
    child.once("close", onClose);

    function onAbort() { stop(130); }
    function stop(code) {
      if (settled || timedOut) return;
      stopCode = code;
      timedOut = true;
      runnerState.pendingCleanups++;
      console.error(`[错误] ${label} ${code === 130 ? "取消" : `超时（${timeoutMs}ms）`}，正在终止本次进程树`);
      const cleanupRemaining = Math.max(1, Math.min(timeoutCleanupMs,
        options.budget?.remainingMs({ cleanup: true }) ?? timeoutCleanupMs));
      cleanupHandle = setTimeout(() => {
        recordCleanupFailure(`超时进程清理未在 ${cleanupRemaining}ms 内确认完成：${label}`);
        finish(stopCode);
      }, cleanupRemaining);

      void (async () => {
        let killed = false;
        try {
          if (child.pid && process.platform === "win32") {
            killed = await killProcessTree(child.pid, cleanupRemaining);
          } else if (typeof child.kill === "function") {
            killed = child.kill("SIGTERM") !== false;
          }
        } catch (error) {
          console.error(`[错误] ${label} 终止失败：${error.message}`);
        }
        let exited = false;
        try {
          exited = Boolean(child.pid) && await waitForExit(child.pid, timeoutCleanupMs, Math.min(250, timeoutCleanupMs));
        } catch (error) {
          console.error(`[错误] ${label} 等待退出失败：${error.message}`);
        }
        if (settled) return;
        cleanupVerified = killed && exited;
        if (cleanupVerified && closed) {
          finish(stopCode);
        }
      })();
    }
    options.signal?.addEventListener("abort", onAbort, { once: true });
    if (options.signal?.aborted) onAbort();
    else if (timeoutMs !== null) timeoutHandle = setTimeout(() => stop(timeoutCode), timeoutMs);
  });
}
