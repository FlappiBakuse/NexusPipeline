using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class AndroidRuntimeFixture : IDisposable
{
    internal const string Subject = "android-runtime-fixture";
    internal ExecutionAdmissionFixture Base { get; }
    internal PlanBuilderFixture Source => Base.Source;
    internal string Directory { get; }
    internal string ToolImage { get; }
    internal string Marker { get; }
    internal string? ScreenshotDirectory { get; }
    internal string Nonce { get; } = Guid.NewGuid().ToString("N");
    internal const string Endpoint = "127.0.0.1:16384";
    internal const string Package = "com.nexus.fixture";
    internal AttemptAcceptanceResult Accepted { get; }
    internal ExecutionRuntimeConsumer Consumer { get; }
    internal ExecutionLiveMedia LiveMedia { get; }
    internal ExecutionAdmissionService Service { get; }
    internal AttemptWriterState State => Source.Lease.PublishExecution(() =>
        AttemptWriterState.Read(Source.Instance, Source.Database, new(), Accepted.JournalId));
    private readonly byte[] _original;
    private readonly PhysicalObjectIdentity _identity;
    private bool _disposed;

    internal AndroidRuntimeFixture(bool configuration = false, bool forceClose = true, bool launch = true, string? responseAction = null,
        string? stdout = null, string? stderr = null, int exitCode = 0, bool hang = false, string? overflowAction = null,
        string? serverAction = null, bool hooks = false, int mainExit = 0, int waitSeconds = 15, string? failAction = null, bool mumu = false,
        bool mainWait = false, bool postWait = false, bool sharedOutput = false, bool accept = true, bool capture = false, string? captureMode = null, bool pairedCapture = false, int stopSeconds = 60)
    {
        var captureRuntime = capture ? new ExecutionJudgementRuntime(Environment.GetEnvironmentVariable("NEXUS_TEST_HOST_EXE") ??
            throw new IOException("capture_fixture_host_missing"), null) : null;
        Base = new(judgementRuntime: captureRuntime);
        try
        {
            var native = Environment.GetEnvironmentVariable("NEXUS_ANDROID_TOOL_FIXTURE_EXE") ?? throw new IOException("android_tool_fixture_missing");
            Directory = Path.Combine(Source.Root, "android tool 空格");
            System.IO.Directory.CreateDirectory(Directory);
            var image = ToolImage = CopyImage(native, Directory);
            Marker = Path.Combine(Source.Root, "android-main");
            if (sharedOutput)
                File.WriteAllBytes(Marker + ".output-fixture.json", JsonSerializer.SerializeToUtf8Bytes(new
                { preBytes = 8388608, mainBytes = 6291456, postBytes = 8388608, logBytes = 2097152, logPath = Source.Log }));
            _original = File.ReadAllBytes(Source.Configuration); _identity = PhysicalObjectIdentity.Read(Source.Configuration);
            File.WriteAllBytes(Path.Combine(Directory, "fixture-device.json"), JsonSerializer.SerializeToUtf8Bytes(new
            {
                driver = mumu ? "mumu" : "genericAdb", endpoint = Endpoint, port = 16384, index = 0, package = Package, nonce = Nonce,
                online = true, foreground = (string?)null, responseAction, responseStandardOutput = stdout,
                responseStandardError = stderr, responseExitCode = exitCode, hangAfterResponse = hang, overflowAction, serverAction, failAction,
                responsePaddingBytes = sharedOutput ? 262144 : 0, screenshotMode = captureMode
            }));
            string? pairedImage = null;
            if (pairedCapture)
            {
                if (!mumu || !capture) throw new IOException("fixture_pair_requires_mumu_capture");
                ScreenshotDirectory = Path.Combine(Directory, "paired ADB 独立");
                System.IO.Directory.CreateDirectory(ScreenshotDirectory);
                pairedImage = CopyImage(native, ScreenshotDirectory);
                File.WriteAllBytes(Path.Combine(ScreenshotDirectory, "fixture-device.json"), JsonSerializer.SerializeToUtf8Bytes(new
                {
                    driver = "genericAdb", endpoint = Endpoint, port = 16384, index = 0, package = Package, nonce = Nonce,
                    online = true, foreground = (string?)null, screenshotMode = captureMode, serverAction
                }));
            }
            var main = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE") ?? throw new IOException("fixture_image_missing");
            var catalog = Source.Definitions.Read();
            var root = new AutomationRoot(Guid.NewGuid(), 0, "Actual Android command", AutomationRootKind.External, Directory);
            var mainRoot = new AutomationRoot(Guid.NewGuid(), 0, "Actual main and hooks", AutomationRootKind.Script, Path.GetDirectoryName(main)!);
            ImmutableArray<string> Arguments(string role, int code) =>
                ["execution-hook", role, Marker, configuration && hooks ? Source.Configuration : "-", code.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    (role == "main" && mainWait || role == "post" && postWait).ToString()];
            var script = catalog.Scripts.Single(item => item.Id == Source.ScriptId) with
            {
                RootId = mainRoot.Id, Executable = new(mainRoot.Id, Path.GetFileName(main)), Arguments = Arguments("main", mainExit),
                LogPaths = sharedOutput ? [new(catalog.Roots.Single(item => item.Path == Path.GetDirectoryName(Source.Log)).Id, Path.GetFileName(Source.Log))] : [],
                ConfigurationPaths = configuration ? catalog.Scripts.Single(item => item.Id == Source.ScriptId).ConfigurationPaths : [],
                Limits = new(1, 30, 600), Judgement = new(AutomationJudgementLanguage.Keywords, ["done"], ["failed"], null),
                Target = new(AutomationTargetKind.Android, launch, waitSeconds, forceClose, null,
                    new(mumu ? AutomationAndroidDriver.Mumu : AutomationAndroidDriver.GenericAdb, new(root.Id, Path.GetFileName(image)), Endpoint, Package, ["-n", Package + "/.Main"])
                    { ScreenshotTool = pairedImage is null ? null : new(root.Id, "paired ADB 独立/" + Path.GetFileName(pairedImage)) })
            };
            var binding = catalog.Bindings.Single(item => item.Id == Source.BindingA) with
            {
                PreHook = hooks ? new(new(mainRoot.Id, Path.GetFileName(main)), Arguments("pre", 0)) : null,
                PostHook = hooks ? new(new(mainRoot.Id, Path.GetFileName(main)), Arguments("post", 0)) : null
            };
            var changes = new AutomationChanges(catalog.Revision, [root, mainRoot], [script], [], [binding], [], [], []);
            Source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), Subject, changes), changes, DateTimeOffset.UtcNow);
            LiveMedia = new(Source.Instance.InstanceId);
            Consumer = new(Source.Instance, Source.Database, new(), Source.Lease, Base.Resources, Base.Index, Base.Inbox, liveMedia: LiveMedia);
            var service = Service = new ExecutionAdmissionService(Source.Instance, Source.Database, new(), Source.Lease,
                Base.Resources, Base.Index, Base.Inbox, new(stopSeconds, 6, 7), judgementRuntime: captureRuntime);
            if (!accept) { Accepted = null!; return; }
            if (mumu)
            {
                using var discovery = PreparedMuMuDiscovery.Prepare(Source.Instance, new(), Source.Lease, Base.Resources, image, Endpoint);
                discovery.DiscoverAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                Accepted = service.StartWithDiscoveries(Guid.NewGuid(), Subject, Source.Request(new BindingTarget(Source.BindingA)),
                    DateTimeOffset.UtcNow, new Dictionary<Guid, PreparedMuMuDiscovery> { [Source.ScriptId] = discovery });
            }
            else Accepted = service.Start(Guid.NewGuid(), Subject, Source.Request(new BindingTarget(Source.BindingA)), DateTimeOffset.UtcNow);
        }
        catch { Base.Dispose(); throw; }
    }

    private static string CopyImage(string source, string directory)
    {
        var frozen = ExecutionHookImages.Capture(source, runtime: true);
        var target = Path.Combine(directory, Path.GetFileName(source)); File.Copy(source, target);
        foreach (var asset in frozen.RuntimeFiles)
        {
            var path = Path.Combine(directory, asset.RelativePath); System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(Path.Combine(Path.GetDirectoryName(source)!, asset.RelativePath), path);
        }
        return target;
    }

    internal async Task<AttemptReceipt> RunAsync()
    {
        var receipt = Assert.IsType<AttemptReceipt>(await Consumer.RunNextAsync(CancellationToken.None)); VerifyOriginal(); return receipt;
    }

    internal Task MainReadyAsync() => RoleReadyAsync("main", TimeSpan.FromSeconds(90));

    internal async Task RoleReadyAsync(string role, TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        while (!File.Exists(Marker + "." + role) && elapsed.Elapsed < timeout) await Task.Delay(10);
        Assert.True(File.Exists(Marker + "." + role));
    }

    internal void Cancel()
    {
        var state = State;
        new ExecutionCancellationStore(Source.Instance, Source.Database, new(), Source.Lease).Cancel(
            ExecutionCancellationStore.Identity(Guid.NewGuid(), Subject, Accepted.JournalId, state.Journal.OperationId,
                Guid.Parse(state.Attempt.RunId), Guid.Parse(state.Attempt.AttemptId)), Accepted.JournalId, DateTimeOffset.UtcNow);
    }

    internal void ChangeReportedPort(int port)
    {
        var path = Path.Combine(Directory, "fixture-device.json");
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        node["reportedPort"] = port; File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(node));
    }

    internal AndroidCollectedDocument Collected()
    {
        var state = State;
        var intent = Assert.Single(AndroidCollectedCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256));
        return new AndroidControlEvidence(Source.Instance, Source.Database, new()).Read<AndroidCollectedDocument>(intent.Artifact);
    }

    internal void VerifyOriginal()
    {
        Assert.Equal(_original, File.ReadAllBytes(Source.Configuration)); Assert.Equal(_identity, PhysicalObjectIdentity.Read(Source.Configuration));
    }

    internal async Task ExecuteAsync(Func<Task> body)
    {
        Exception? original = null;
        try { await body(); } catch (Exception error) { original = error; }
        try { Dispose(); } catch (Exception cleanup) { if (original is not null) throw new AggregateException(original, cleanup); throw; }
        if (original is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(original).Throw();
    }

    public void Dispose()
    {
        if (_disposed) return;
        Base.Dispose(); _disposed = true;
    }
}
