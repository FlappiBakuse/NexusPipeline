using System.Diagnostics;
using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class PcStageFixture : IDisposable
{
    internal const string Subject = "pc-target-stage-fixture";
    internal ExecutionAdmissionFixture Base { get; }
    private readonly bool _nativeWindow;
    internal PlanBuilderFixture Source => Base.Source;
    internal string HookMarker { get; }
    internal string GameMarker { get; }
    internal string GameImage { get; }
    internal string GameExitSignal { get; }
    internal string Nonce { get; } = Guid.NewGuid().ToString("N");
    internal ExecutionRuntimeConsumer Consumer { get; }
    internal ExecutionLiveMedia LiveMedia { get; }
    private AttemptAcceptanceResult? _accepted;
    internal AttemptAcceptanceResult Accepted => _accepted ?? throw new InvalidOperationException("fixture_not_accepted");
    internal AttemptWriterState State => Source.Lease.PublishExecution(() =>
        AttemptWriterState.Read(Source.Instance, Source.Database, new(), Accepted.JournalId));
    private OwnedWorkerJob? _external;
    private Process? _game;
    private readonly List<SafeKernelHandle> _ownedFamily = [];
    private bool _disposed;

    internal PcStageFixture(bool configuration = false, bool launch = true, bool forceClose = true,
        string targetMode = "ready", bool mainWait = false, bool external = false, bool hooks = true, bool distinctConfiguration = false,
        bool gameCanExit = false, bool accept = true, bool nativeWindow = false, int totalBudgetSeconds = 120,
        int captureFrames = 1, bool distinctCaptureFrames = true)
    {
        if (totalBudgetSeconds is < 1 or > 120) throw new ArgumentOutOfRangeException(nameof(totalBudgetSeconds));
        _nativeWindow = nativeWindow;
        Base = new(judgementRuntime: nativeWindow ? new ExecutionJudgementRuntime(
            Environment.GetEnvironmentVariable("NEXUS_TEST_HOST_EXE")!, null) { CaptureFrameCount = captureFrames } : null);
        try
        {
            HookMarker = Path.Combine(Source.Root, "pc-hooks");
            GameMarker = Path.Combine(Source.Root, "pc-game");
            GameExitSignal = Path.Combine(Source.Root, "pc-game-exit");
            if (distinctConfiguration) File.WriteAllText(Source.Configuration, "{\"fixture\":\"external\"}");
            var actual = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE") ?? throw new IOException("fixture_image_missing");
            var game = nativeWindow ? actual : Environment.GetEnvironmentVariable("NEXUS_TARGET_GAME_FIXTURE_EXE") ?? throw new IOException("target_game_fixture_missing");
            var gameDirectory = Path.Combine(Source.Root, "game 空格");
            Directory.CreateDirectory(gameDirectory);
            GameImage = CopyImage(game, gameDirectory);
            var catalog = Source.Definitions.Read();
            var script = catalog.Scripts.Single(item => item.Id == Source.ScriptId);
            var root = new AutomationRoot(Guid.NewGuid(), 0, "Main and hooks", AutomationRootKind.Script, Path.GetDirectoryName(actual)!);
            var targetRoot = new AutomationRoot(Guid.NewGuid(), 0, "Target game", AutomationRootKind.External, gameDirectory);
            ImmutableArray<string> windowArgs = distinctCaptureFrames ? ["capture-window-fixture", GameMarker] :
                ["capture-window-fixture", GameMarker, "static-frame"];
            var launchPath = new AutomationPath(targetRoot.Id, Path.GetFileName(GameImage));
            var readyPath = launchPath;
            var extra = new List<AutomationRoot>();
            if (targetMode == "invalid")
            {
                File.WriteAllText(Path.Combine(gameDirectory, "invalid.exe"), "invalid frozen PE image");
                launchPath = new(targetRoot.Id, "invalid.exe");
            }
            if (targetMode == "timeout")
            {
                var missingGameDirectory = Path.Combine(Source.Root, "not-running game");
                Directory.CreateDirectory(missingGameDirectory);
                var readyRoot = new AutomationRoot(Guid.NewGuid(), 0, "Unstarted ready image", AutomationRootKind.External, missingGameDirectory);
                readyPath = new(readyRoot.Id, Path.GetFileName(CopyImage(game, missingGameDirectory)));
                extra.Add(readyRoot);
            }
            if (targetMode == "launcher")
            {
                var directory = Path.Combine(Source.Root, "launcher 空格");
                Directory.CreateDirectory(directory);
                var launcher = new AutomationRoot(Guid.NewGuid(), 0, "GUI launcher", AutomationRootKind.External, directory);
                launchPath = new(launcher.Id, Path.GetFileName(CopyImage(game, directory)));
                extra.Add(launcher);
            }
            var binding = catalog.Bindings.Single(item => item.Id == Source.BindingA) with
            {
                PreHook = hooks ? new(new(root.Id, Path.GetFileName(actual)), Hook("pre", false)) : null,
                PostHook = hooks ? new(new(root.Id, Path.GetFileName(actual)), Hook("post", false)) : null
            };
            script = script with
            {
                RootId = root.Id, Executable = new(root.Id, Path.GetFileName(actual)),
                Arguments = Hook("main", mainWait), ConfigurationPaths = configuration ? script.ConfigurationPaths : [],
                LogPaths = [], Limits = new(1, Math.Min(30, totalBudgetSeconds), totalBudgetSeconds), Judgement = new(AutomationJudgementLanguage.Keywords, ["done"], ["failed"], null),
                Target = new(AutomationTargetKind.Pc, launch, targetMode == "timeout" ? 1 : 10, forceClose,
                    new(launchPath, nativeWindow ? windowArgs : targetMode == "launcher" ? ["--launch-child", GameImage, GameMarker, Nonce] :
                        gameCanExit ? [GameMarker, Nonce, configuration && !hooks ? Source.Configuration : "-", GameExitSignal] :
                        configuration && !hooks ? [GameMarker, Nonce, Source.Configuration] : [GameMarker, Nonce], readyPath, nativeWindow ? AutomationPcReadiness.VisibleWindow : AutomationPcReadiness.Process), null)
            };
            var changes = new AutomationChanges(catalog.Revision, new[] { root, targetRoot }.Concat(extra).ToImmutableArray(),
                [script], [], [binding], [], [], []);
            Source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), Subject, changes), changes, DateTimeOffset.UtcNow);
            if (external)
            {
                _external = new(); _external.ObserveProcessIdentities(_ => { });
                _external.Start(GameImage, nativeWindow ? windowArgs : [GameMarker, Nonce],
                    gameDirectory, Guid.NewGuid(), Guid.NewGuid(), _ => { });
                AwaitGameAsync().GetAwaiter().GetResult();
            }
            LiveMedia = new(Source.Instance.InstanceId);
            Consumer = new(Source.Instance, Source.Database, new(), Source.Lease, Base.Resources, Base.Index, Base.Inbox, liveMedia: LiveMedia);
            if (accept) Accept();
        }
        catch (Exception original)
        {
            try { Dispose(); }
            catch (Exception cleanup) { throw new AggregateException(original, cleanup); }
            throw;
        }

        System.Collections.Immutable.ImmutableArray<string> Hook(string role, bool wait) =>
            ["execution-hook", role, HookMarker, configuration && hooks ? Source.Configuration : "-", "0", wait.ToString()];
    }

    internal void Accept()
    {
        if (_accepted is not null) throw new InvalidOperationException("fixture_already_accepted");
        _accepted = Base.Service.Start(Guid.NewGuid(), Subject, Source.Request(new BindingTarget(Source.BindingA)), DateTimeOffset.UtcNow);
    }

    private static string CopyImage(string source, string directory)
    {
        var image = ExecutionHookImages.Capture(source, runtime: true);
        var path = Path.Combine(directory, Path.GetFileName(source)); File.Copy(source, path);
        foreach (var file in image.RuntimeFiles)
        {
            var target = Path.Combine(directory, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(Path.GetDirectoryName(source)!, file.RelativePath), target);
        }
        return path;
    }

    internal async Task AwaitGameAsync()
    {
        var timer = Stopwatch.StartNew();
        while (!File.Exists(GameMarker) && timer.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(10);
        Assert.True(File.Exists(GameMarker));
        var bytes = _nativeWindow ? JsonSerializer.SerializeToUtf8Bytes(await OwnedCrashProcess.ReadReportAsync(GameMarker, TimeSpan.FromSeconds(15)))
            : File.ReadAllBytes(GameMarker);
        using var document = JsonDocument.Parse(bytes);
        if (_nativeWindow) Assert.Equal("windowReady", document.RootElement.GetProperty("status").GetString());
        else Assert.Equal(Nonce, document.RootElement.GetProperty("nonce").GetString());
        _game ??= Process.GetProcessById(document.RootElement.GetProperty("processId").GetInt32());
        _ = _game.SafeHandle;
        Assert.Equal(Path.GetFullPath(GameImage), _game.MainModule!.FileName, ignoreCase: true);
    }

    internal async Task AwaitMainAsync()
    {
        var timer = Stopwatch.StartNew();
        while (!File.Exists(HookMarker + ".main") && timer.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(10);
        Assert.True(File.Exists(HookMarker + ".main")); await AwaitGameAsync();
    }

    internal void Cancel()
    {
        var state = State;
        new ExecutionCancellationStore(Source.Instance, Source.Database, new(), Source.Lease).Cancel(
            ExecutionCancellationStore.Identity(Guid.NewGuid(), Subject, Accepted.JournalId, state.Journal.OperationId,
                Guid.Parse(state.Attempt.RunId), Guid.Parse(state.Attempt.AttemptId)), Accepted.JournalId, DateTimeOffset.UtcNow);
    }

    internal bool GameIsAlive => _game is not null && !_game.HasExited;

    internal async Task ExitGameAsync()
    {
        Assert.NotNull(_game); File.WriteAllText(GameExitSignal, Nonce);
        await _game.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(GameIsAlive);
    }

    internal async Task ExecuteAsync(Func<Task> body)
    {
        Exception? original = null;
        try { await body().ConfigureAwait(false); } catch (Exception error) { original = error; }
        try { Dispose(); }
        catch (Exception cleanup) { if (original is not null) throw new AggregateException(original, cleanup); throw; }
        if (original is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(original).Throw();
    }

    private void HoldCleanupGame()
    {
        var state = State;
        var target = TargetJobCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256).SingleOrDefault();
        if (target is null) return;
        var candidates = new List<ProcessIdentity>();
        if (TargetRootFacts.Read(Source.Database, target.Binding) is { } root) candidates.Add(root);
        var evidence = new TargetJobEvidence(Source.Instance, Source.Database, new());
        if (target.Stage == "retained") candidates.AddRange(evidence.Read<TargetRetainedDocument>(target.Evidence!).Native.Inventory.AllFamily);
        if (target.Stage == "retentionPrepared") candidates.AddRange(evidence.Read<TargetRetentionDocument>(target.Evidence!).Inventory.AllFamily);
        foreach (var identity in candidates.Distinct())
        {
            var process = JobNative.OpenProcess(0x101001, false, identity.ProcessId);
            try
            {
                if (process.IsInvalid || JobNative.WaitForSingleObject(process, 0) != 258 ||
                    ProcessIdentityReader.Read(process, identity.ProcessId) != identity || JobNative.WaitForSingleObject(process, 0) != 258) continue;
                _ownedFamily.Add(process); process = null!;
            }
            finally { process?.Dispose(); }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Exception? failure = null;
        try
        {
            if (_external is not null)
            {
                if (!_external.StopAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult()) throw new IOException("fixture_external_stop_unknown");
                _external.Dispose();
            }
            if (_accepted is not null) HoldCleanupGame();
            foreach (var member in _ownedFamily)
                if (JobNative.WaitForSingleObject(member, 0) == 258 &&
                    (!JobNative.TerminateProcess(member, 1) || JobNative.WaitForSingleObject(member, 10000) != 0))
                    throw new IOException("fixture_retained_game_cleanup_pending");
            foreach (var member in _ownedFamily) member.Dispose();
            _game?.Dispose();
        }
        catch (Exception error) { failure = error; }
        try { Base.Dispose(); }
        catch (Exception cleanup) { failure = failure is null ? cleanup : new AggregateException(failure, cleanup); }
        _disposed = true;
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
