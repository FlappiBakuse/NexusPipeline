using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Security;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Host.Desktop;

[SupportedOSPlatform("windows")]
internal sealed class StartupProgress : IDisposable
{
    private readonly long _started = Stopwatch.GetTimestamp();
    private long _stageStarted = Stopwatch.GetTimestamp();
    private StartupStage _stage;
    private readonly StartupChannel _channel = new();
    private readonly DesktopResponseSigner _signer = new();
    private readonly RuntimeInstance _instance;
    private readonly Task<OwnedDesktop> _desktop;
    internal OwnedDesktop Desktop => _desktop.GetAwaiter().GetResult();
    internal UpdateWindowIntent Intent { get; }
    internal DesktopResponseSigner Signer => _signer;

    internal StartupProgress(RuntimeInstance instance, string payloadRoot, InstanceLease lease,
        UpdateWindowIntent? resume, bool manual)
    {
        _instance = instance;
        try
        {
            var presentation = StartupPresentation.Read(instance, lease);
            Intent = resume ?? new(presentation.Desktop.Mode == DesktopMode.Desktop ? UpdateWindowMode.Desktop : UpdateWindowMode.Browser,
                manual || presentation.Desktop.OpenOnStartup, "/");
            Advance(StartupStage.OpeningInstance);
            _desktop = Task.Run(() =>
            {
                using var profile = StartupProfile.Begin("desktop.launch");
                var desktop = new OwnedDesktop(instance, payloadRoot, _signer, Intent, presentation.Desktop.MinimizeToTray, _channel,
                    presentation.Locale == HostLocale.ZhCn ? "zh-CN" : "en-US");
                try { desktop.Start(); return desktop; }
                catch { desktop.Dispose(); throw; }
            });
        }
        catch { _channel.Dispose(); _signer.Dispose(); throw; }
    }

    internal void Activate() => _channel.Publish(new { kind = "activate" });

    internal void Advance(StartupStage stage)
    {
        var previous = _stage;
        var started = _stageStarted;
        _stage = stage; _stageStarted = Stopwatch.GetTimestamp();
        Console.Error.WriteLine(JsonSerializer.Serialize(new { kind = "startup.stage", stage = previous.ToString(),
            elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            totalMilliseconds = Stopwatch.GetElapsedTime(_started).TotalMilliseconds, next = stage.ToString() }));
        _channel.Publish(new { kind = "stage", stage = (int)stage, total = (int)StartupStage.Ready + 1,
            elapsedMilliseconds = Stopwatch.GetElapsedTime(_started).TotalMilliseconds });
    }

    internal async Task ConfigureAsync(int port, DesktopPreferences preferences)
    {
        await _channel.ReadyAsync(new { address = "http://127.0.0.1:" + port,
            mode = Intent.Mode == UpdateWindowMode.Browser ? "browser" : "desktop",
            minimizeToTray = preferences.MinimizeToTray, route = Intent.Route });
    }

    internal async Task CompleteAsync()
    {
        Advance(StartupStage.Ready);
        await _channel.SendAsync(new { kind = "complete" });
    }

    internal async Task FailAsync(Exception error)
    {
        var reason = Reason(error);
        var code = error is Win32Exception native ? native.NativeErrorCode : error.HResult;
        Console.Error.WriteLine(JsonSerializer.Serialize(new { kind = "host.startup.failed", reason, errorCode = code }));
        try
        {
            await _desktop;
            await _channel.FailAsync(reason, code).WaitAsync(TimeSpan.FromSeconds(5));
            await _channel.Dismissed;
        }
        catch (Exception failure) when (_desktop.IsFaulted || failure is IOException or TimeoutException or OperationCanceledException)
        { ShowFailure(reason, code, _instance.RuntimeRoot); }
    }

    internal static string Reason(Exception error) => error.Message.Length is > 0 and <= 160 &&
        error.Message.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')
            ? error.Message : error.GetType().Name;

    internal static void ShowFailure(string reason, int errorCode, string runtimeRoot)
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(new { kind = "host.startup.failed", reason, errorCode }));
        if (!Environment.UserInteractive) return;
        MessageBoxW(IntPtr.Zero, $"NexusPipeline 启动失败 / Startup failed\n{reason}\n0x{errorCode:X8}\n{runtimeRoot}", "NexusPipeline", 0x10);
    }

    public void Dispose()
    {
        OwnedDesktop? desktop = null;
        try { desktop = _desktop.GetAwaiter().GetResult(); }
        // Startup or failure presentation observes launch faults; cleanup still joins the owner before releasing its channel.
        catch (Exception) when (_desktop.IsFaulted) { }
        finally
        {
            _channel.Dispose();
            try { desktop?.Dispose(); }
            finally { _signer.Dispose(); }
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr window, string text, string title, uint type);
}
