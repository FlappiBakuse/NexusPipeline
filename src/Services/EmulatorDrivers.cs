using System.Text.Json.Nodes;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services;

internal enum EmulatorKind
{
    GenericAdb,
    MuMu,
    DetectionError,
}

/// <summary>一次运行冻结的模拟器目标。创建后不允许在 GenericAdb 与 MuMu 之间切换。</summary>
internal sealed record EmulatorTarget(
    EmulatorKind Kind,
    string Endpoint,
    string? AdbExecutable = null,
    string? MuMuManagerPath = null,
    string? MuMuInstanceIndex = null,
    string? DetectionError = null);

internal sealed record EmulatorCommandResult(bool Ok, string Output)
{
    public static EmulatorCommandResult Success(string output = "") => new(true, output);

    public static EmulatorCommandResult Failure(string output) => new(false, output);
}

internal sealed record EmulatorBinaryResult(bool Ok, byte[] Data, string Error)
{
    public static EmulatorBinaryResult Success(byte[] data) => new(true, data, "");

    public static EmulatorBinaryResult Failure(string error) => new(false, Array.Empty<byte>(), error);
}

internal interface IEmulatorDriver
{
    EmulatorKind Kind { get; }

    Task<EmulatorCommandResult> EnsureReadyAsync(CancellationToken token, int timeoutSeconds);

    Task<EmulatorCommandResult> StartAppAsync(IReadOnlyList<string> startArgs, CancellationToken token, int timeoutSeconds);

    Task<string?> GetForegroundPackageAsync(CancellationToken token, int timeoutSeconds);

    Task<EmulatorBinaryResult> CaptureScreenAsync(CancellationToken token, int timeoutSeconds);

    Task<EmulatorCommandResult> StopAppAsync(string? packageName, CancellationToken token, int timeoutSeconds);

    Task<EmulatorCommandResult> ShutdownAsync(CancellationToken token, int timeoutSeconds);
}

internal static class EmulatorDetector
{
    /// <summary>
    /// MuMuManager 缺失或 info 成功但端口未匹配时才返回 GenericAdb；
    /// manager 存在但 info 失败时返回 DetectionError，避免错误地走 ADB。
    /// </summary>
    public static async Task<EmulatorTarget> DetectAsync(string endpoint, CancellationToken token, int timeoutSeconds)
    {
        string normalized = endpoint?.Trim() ?? "";
        string? adb = EmulatorSupport.ResolveAdbExe();
        if (!EmulatorSupport.IsValidAdbAddress(normalized))
        {
            return new EmulatorTarget(EmulatorKind.DetectionError, normalized, adb, DetectionError: "模拟器ADB地址格式不正确（应为 主机:端口）");
        }
        if (!EmulatorSupport.IsLoopbackAdbEndpoint(normalized) || EmulatorSupport.ParseAdbPort(normalized) is not int port)
        {
            return Generic(normalized, adb);
        }
        string? manager = EmulatorSupport.ResolveMuMuManager();
        if (manager is null)
        {
            return Generic(normalized, adb);
        }
        (bool ok, string output) = await EmulatorSupport.RunCommandAsync(
            manager,
            new[] { "info", "-v", "all" },
            timeoutSeconds,
            token).ConfigureAwait(false);
        if (!ok)
        {
            return new EmulatorTarget(
                EmulatorKind.DetectionError,
                normalized,
                adb,
                manager,
                DetectionError: $"MuMuManager info 失败：{output.Trim()}");
        }
        string? index = EmulatorSupport.ParseMuMuVmIndex(output, port);
        return index is null
            ? Generic(normalized, adb)
            : new EmulatorTarget(EmulatorKind.MuMu, normalized, null, manager, index);
    }

    private static EmulatorTarget Generic(string endpoint, string? adb)
    {
        return new EmulatorTarget(EmulatorKind.GenericAdb, endpoint, adb);
    }
}

internal static class EmulatorDriverFactory
{
    public static IEmulatorDriver Create(EmulatorTarget target)
    {
        return target.Kind switch
        {
            EmulatorKind.GenericAdb => new GenericAdbEmulatorDriver(target),
            EmulatorKind.MuMu => new MuMuEmulatorDriver(target),
            EmulatorKind.DetectionError => throw new InvalidOperationException(target.DetectionError ?? "模拟器目标识别失败"),
            _ => throw new ArgumentOutOfRangeException(nameof(target), target.Kind, "未知模拟器类型"),
        };
    }
}

internal sealed class GenericAdbEmulatorDriver : IEmulatorDriver
{
    private readonly EmulatorTarget _target;

    public GenericAdbEmulatorDriver(EmulatorTarget target)
    {
        if (target.Kind != EmulatorKind.GenericAdb)
        {
            throw new ArgumentException("Generic ADB driver 只能绑定 GenericAdb target。", nameof(target));
        }
        _target = target;
    }

    public EmulatorKind Kind => EmulatorKind.GenericAdb;

    public async Task<EmulatorCommandResult> EnsureReadyAsync(CancellationToken token, int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(_target.AdbExecutable))
        {
            return EmulatorCommandResult.Failure("未找到 adb 可执行文件（请安装 Android 平台工具或模拟器）");
        }
        (bool ok, string output) = await EmulatorSupport.AdbConnectAsync(
            _target.AdbExecutable,
            _target.Endpoint,
            token,
            timeoutSeconds).ConfigureAwait(false);
        return ok ? EmulatorCommandResult.Success(output) : EmulatorCommandResult.Failure(output);
    }

    public async Task<EmulatorCommandResult> StartAppAsync(IReadOnlyList<string> startArgs, CancellationToken token, int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(_target.AdbExecutable))
        {
            return EmulatorCommandResult.Failure("未找到 adb 可执行文件");
        }
        var shellArgs = new List<string> { "am", "start" };
        shellArgs.AddRange(startArgs);
        (bool ok, string output) = await EmulatorSupport.AdbShellAsync(
            _target.AdbExecutable,
            _target.Endpoint,
            shellArgs.ToArray(),
            timeoutSeconds,
            token).ConfigureAwait(false);
        return ok && !EmulatorSupport.AmStartFailed(output)
            ? EmulatorCommandResult.Success(output)
            : EmulatorCommandResult.Failure(output);
    }

    public Task<string?> GetForegroundPackageAsync(CancellationToken token, int timeoutSeconds)
    {
        return string.IsNullOrWhiteSpace(_target.AdbExecutable)
            ? Task.FromResult<string?>(null)
            : EmulatorSupport.GetForegroundPackageAsync(_target.AdbExecutable, _target.Endpoint, token, timeoutSeconds);
    }

    public async Task<EmulatorBinaryResult> CaptureScreenAsync(CancellationToken token, int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(_target.AdbExecutable))
        {
            return EmulatorBinaryResult.Failure("未找到 adb 可执行文件");
        }
        EmulatorBinaryResult result = await EmulatorSupport.RunBinaryCommandAsync(
            _target.AdbExecutable,
            new[] { "-s", _target.Endpoint, "exec-out", "screencap", "-p" },
            timeoutSeconds,
            token).ConfigureAwait(false);
        return result.Ok && EmulatorSupport.IsPng(result.Data)
            ? result
            : EmulatorBinaryResult.Failure(result.Error.Length > 0 ? result.Error : "ADB 未返回有效 PNG 截图");
    }

    public async Task<EmulatorCommandResult> StopAppAsync(string? packageName, CancellationToken token, int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(packageName))
        {
            return EmulatorCommandResult.Success("未配置目标包名，跳过应用关闭");
        }
        if (IsProtectedPackage(packageName))
        {
            return EmulatorCommandResult.Success($"目标包名为系统桌面（{packageName}），跳过应用关闭");
        }
        if (string.IsNullOrWhiteSpace(_target.AdbExecutable))
        {
            return EmulatorCommandResult.Failure("未找到 adb 可执行文件");
        }
        (bool ok, string output) = await EmulatorSupport.AdbShellAsync(
            _target.AdbExecutable,
            _target.Endpoint,
            new[] { "am", "force-stop", packageName },
            timeoutSeconds,
            token).ConfigureAwait(false);
        return ok ? EmulatorCommandResult.Success(output) : EmulatorCommandResult.Failure(output);
    }

    public async Task<EmulatorCommandResult> ShutdownAsync(CancellationToken token, int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(_target.AdbExecutable))
        {
            return EmulatorCommandResult.Failure("未找到 adb 可执行文件");
        }
        (bool ok, string message) = await EmulatorSupport.ShutdownGenericEmulatorAsync(
            _target.AdbExecutable,
            _target.Endpoint,
            token).ConfigureAwait(false);
        return ok ? EmulatorCommandResult.Success(message) : EmulatorCommandResult.Failure(message);
    }

    private static bool IsProtectedPackage(string packageName)
    {
        return packageName is "com.android.systemui" or "com.android.launcher" or "app.lawnchair" or "com.mumu.launcher";
    }
}

internal sealed class MuMuEmulatorDriver : IEmulatorDriver
{
    private readonly EmulatorTarget _target;

    public MuMuEmulatorDriver(EmulatorTarget target)
    {
        if (target.Kind != EmulatorKind.MuMu || string.IsNullOrWhiteSpace(target.MuMuManagerPath) || string.IsNullOrWhiteSpace(target.MuMuInstanceIndex))
        {
            throw new ArgumentException("MuMu driver 需要绑定完整 MuMu target。", nameof(target));
        }
        _target = target;
    }

    public EmulatorKind Kind => EmulatorKind.MuMu;

    public async Task<EmulatorCommandResult> EnsureReadyAsync(CancellationToken token, int timeoutSeconds)
    {
        // MuMu 目标从此只使用 MuMuManager.exe；这里绝不解析或启动 adb.exe。
        EmulatorCommandResult launch = await RunManagerAsync(new[] { "control", "-v", _target.MuMuInstanceIndex!, "launch" }, token, timeoutSeconds).ConfigureAwait(false);
        if (!launch.Ok && !LooksAlreadyRunning(launch.Output))
        {
            return launch;
        }
        EmulatorCommandResult connect = await RunManagerAsync(new[] { "adb", "-v", _target.MuMuInstanceIndex!, "connect" }, token, timeoutSeconds).ConfigureAwait(false);
        return connect;
    }

    public async Task<EmulatorCommandResult> StartAppAsync(IReadOnlyList<string> startArgs, CancellationToken token, int timeoutSeconds)
    {
        var args = new List<string> { "adb", "-v", _target.MuMuInstanceIndex!, "shell", "am", "start" };
        args.AddRange(startArgs);
        EmulatorCommandResult result = await RunManagerAsync(args, token, timeoutSeconds).ConfigureAwait(false);
        return result.Ok && !EmulatorSupport.AmStartFailed(result.Output)
            ? result
            : EmulatorCommandResult.Failure(result.Output);
    }

    public async Task<string?> GetForegroundPackageAsync(CancellationToken token, int timeoutSeconds)
    {
        EmulatorCommandResult result = await RunManagerAsync(
            new[] { "adb", "-v", _target.MuMuInstanceIndex!, "shell", "dumpsys", "window" },
            token,
            timeoutSeconds).ConfigureAwait(false);
        return result.Ok ? EmulatorSupport.ParseForegroundPackage(result.Output) : null;
    }

    public async Task<EmulatorBinaryResult> CaptureScreenAsync(CancellationToken token, int timeoutSeconds)
    {
        EmulatorBinaryResult direct = await RunManagerBinaryAsync(
            new[] { "adb", "-v", _target.MuMuInstanceIndex!, "exec-out", "screencap", "-p" },
            token,
            timeoutSeconds).ConfigureAwait(false);
        if (direct.Ok && EmulatorSupport.IsPng(direct.Data))
        {
            return direct;
        }
        EmulatorBinaryResult fallback = await RunManagerBinaryAsync(
            new[] { "adb", "-v", _target.MuMuInstanceIndex!, "shell", "screencap", "-p" },
            token,
            timeoutSeconds).ConfigureAwait(false);
        return fallback.Ok && EmulatorSupport.IsPng(fallback.Data)
            ? fallback
            : EmulatorBinaryResult.Failure(fallback.Error.Length > 0 ? fallback.Error : (direct.Error.Length > 0 ? direct.Error : "MuMuManager 未返回有效 PNG 截图"));
    }

    public async Task<EmulatorCommandResult> StopAppAsync(string? packageName, CancellationToken token, int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(packageName))
        {
            return EmulatorCommandResult.Success("未配置目标包名，跳过应用关闭");
        }
        if (packageName is "com.android.systemui" or "com.android.launcher" or "app.lawnchair" or "com.mumu.launcher")
        {
            return EmulatorCommandResult.Success($"目标包名为系统桌面（{packageName}），跳过应用关闭");
        }
        // MuMuManager 的 API 命令由 manager 自身转发到对应实例，不调用 adb.exe。
        return await RunManagerAsync(
            new[] { "api", "-v", _target.MuMuInstanceIndex!, "close_app", packageName },
            token,
            timeoutSeconds).ConfigureAwait(false);
    }

    public async Task<EmulatorCommandResult> ShutdownAsync(CancellationToken token, int timeoutSeconds)
    {
        EmulatorCommandResult shutdown = await RunManagerAsync(
            new[] { "control", "-v", _target.MuMuInstanceIndex!, "shutdown" },
            token,
            timeoutSeconds).ConfigureAwait(false);
        if (!shutdown.Ok)
        {
            return shutdown;
        }
        bool offline = await WaitStoppedAsync(token, timeoutSeconds).ConfigureAwait(false);
        return offline
            ? EmulatorCommandResult.Success($"已通过 MuMuManager 关闭模拟器（实例索引 {_target.MuMuInstanceIndex}）")
            : EmulatorCommandResult.Failure($"MuMuManager 已发送关闭指令，但实例 {_target.MuMuInstanceIndex} 未确认离线");
    }

    private Task<EmulatorCommandResult> RunManagerAsync(IReadOnlyList<string> args, CancellationToken token, int timeoutSeconds)
    {
        return RunManagerCoreAsync(args, token, timeoutSeconds);
    }

    private async Task<EmulatorCommandResult> RunManagerCoreAsync(IReadOnlyList<string> args, CancellationToken token, int timeoutSeconds)
    {
        (bool ok, string output) = await EmulatorSupport.RunCommandAsync(_target.MuMuManagerPath!, args, timeoutSeconds, token).ConfigureAwait(false);
        return ok ? EmulatorCommandResult.Success(output) : EmulatorCommandResult.Failure(output);
    }

    private async Task<EmulatorBinaryResult> RunManagerBinaryAsync(IReadOnlyList<string> args, CancellationToken token, int timeoutSeconds)
    {
        return await EmulatorSupport.RunBinaryCommandAsync(
            _target.MuMuManagerPath!,
            args,
            timeoutSeconds,
            token).ConfigureAwait(false);
    }

    private async Task<bool> WaitStoppedAsync(CancellationToken token, int timeoutSeconds)
    {
        DateTime deadline = DateTime.Now.AddSeconds(TestHooks.ScaledSeconds(Math.Max(1, Math.Min(60, timeoutSeconds * 2))));
        while (DateTime.Now < deadline)
        {
            EmulatorCommandResult state = await RunManagerAsync(
                new[] { "info", "-v", _target.MuMuInstanceIndex! },
                token,
                timeoutSeconds).ConfigureAwait(false);
            if (state.Ok && IsStoppedState(state.Output))
            {
                return true;
            }
            await Task.Delay(TestHooks.ScaledMs(1000), token).ConfigureAwait(false);
        }
        return false;
    }

    private static bool IsStoppedState(string output)
    {
        try
        {
            if (JsonNode.Parse(output) is JsonObject root)
            {
                var candidates = new List<JsonObject> { root };
                foreach (KeyValuePair<string, JsonNode?> property in root)
                {
                    if (property.Value is JsonObject child)
                    {
                        candidates.Add(child);
                    }
                }
                foreach (JsonObject candidate in candidates)
                {
                    bool? process = ReadBool(candidate, "is_process_started", "process_started", "is_running", "running");
                    bool? android = ReadBool(candidate, "is_android_started", "android_started");
                    if (process is false && (android is null or false))
                    {
                        return true;
                    }
                    if (process is true || android is true)
                    {
                        return false;
                    }
                }
            }
        }
        catch
        {
            return false;
        }
        string text = output.Trim();
        return text.Contains("stopped", StringComparison.OrdinalIgnoreCase)
            || text.Contains("not running", StringComparison.OrdinalIgnoreCase)
            || text.Equals("0", StringComparison.OrdinalIgnoreCase);
    }

    private static bool? ReadBool(JsonObject root, params string[] names)
    {
        foreach (string name in names)
        {
            if (root[name] is JsonValue value)
            {
                if (value.TryGetValue<bool>(out bool boolean))
                {
                    return boolean;
                }
                if (value.TryGetValue<int>(out int number))
                {
                    return number != 0;
                }
                string text = value.ToString();
                if (text.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
                if (text.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            }
        }
        return null;
    }

    private static bool LooksAlreadyRunning(string output)
    {
        return output.Contains("already", StringComparison.OrdinalIgnoreCase)
            || output.Contains("running", StringComparison.OrdinalIgnoreCase);
    }
}
