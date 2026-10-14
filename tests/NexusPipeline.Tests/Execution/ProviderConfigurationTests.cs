using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.Execution.Realtime;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Scripts.Contracts;
using NexusPipeline.Modules.Scripts.UseCases;
using NexusPipeline.Modules.Users.Contracts;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Shared.Results;
using NexusPipeline.Tests.Support;
using NexusPipeline.Host.Composition;
using Xunit;

namespace NexusPipeline.Tests.Execution;

public sealed class ProviderConfigurationTests : IClassFixture<HostTestScope>
{
    public ProviderConfigurationTests(HostTestScope scope) { }

    [Fact]
    public async Task UserInitializationCommitsOnlyItsBindingAndRejectsConcurrentChanges()
    {
        using var fixture = new ProviderWorkerFixture();
        foreach (bool concurrent in new[] { false, true })
        {
            var repository = new Repository { Visible = new() { Id = "existing", Name = "Shared", RootPath = fixture.Root,
                ExecutionProviderId = "fixture", ExecutionProviderConfigId = "profile", ExecutionProviderConfigRevision = "shared" } };
            var users = new UserStore(repository); var provider = new Provider(); var events = new RealtimeEventBus();
            if (concurrent) provider.OnCommit = () => users.Fingerprint = new string('b', 64);
            string journals = Path.Combine(fixture.Root, "user-" + concurrent);
            var request = new ProviderConfigurationSaveRequest(Guid.NewGuid().ToString("N"), new() { Id = "existing" }, new())
                { UserId = "user", ExpectedUserRevision = "previous_user" };
            await using (var operations = new ProviderConfigurationOperations(repository, new Providers(provider, fixture.Root), Dispatcher(new(), events), events,
                journals, users: users))
            {
                Assert.Throws<InvalidDataException>(() => operations.Start(request with { ExpectedUserRevision = "stale" }, repository.Commit));
                var status = await Wait(operations, operations.Start(request, repository.Commit).OperationId);
                Assert.Equal(concurrent ? "needs_attention" : "ready", status.State);
                Assert.True(status.CleanupConfirmed); Assert.Equal("user", status.UserId);
                Assert.Equal("shared", repository.Visible.ExecutionProviderConfigRevision);
                Assert.Equal(concurrent ? "previous_user" : status.Revision, users.Revision);
                Assert.Equal("previous_user", provider.LastStage!.ExpectedRevision);
                Assert.Equal("shared", provider.LastStage.Configuration["sharedRevision"]!.GetValue<string>());
                Assert.Equal(1, provider.Initializations);
            }
            if (!concurrent)
            {
                string file = Path.Combine(journals, request.OperationId + ".json");
                File.Move(Path.Combine(journals, "completed", request.OperationId + ".json"), file);
                var journal = JsonSerializer.Deserialize<ProviderConfigurationJournal>(File.ReadAllText(file), ProviderConfigurationOperations.Json)!;
                File.WriteAllText(file, JsonSerializer.Serialize(journal with { State = "plugin_committed" }, ProviderConfigurationOperations.Json));
                await using var recovered = new ProviderConfigurationOperations(repository, new Providers(provider, fixture.Root), Dispatcher(new(), events), events,
                    journals, users: users);
                Assert.Equal("ready", recovered.Find(request.OperationId)!.State);
                Assert.Equal(1, provider.Initializations);
            }
        }
    }

    private sealed class UserStore(Repository scripts) : IUserProviderConfigurationStore
    {
        internal int BindingCount = 1;
        internal string Revision = "previous_user", Fingerprint = new('a', 64);
        public UserProviderConfigurationBinding? FindProviderBinding(string userId, string scriptId) => userId == "user" && scriptId == "existing"
            ? new(userId, scriptId, Revision, Fingerprint) : null;
        public IReadOnlyList<UserProviderConfigurationBinding> ProviderBindings() => Enumerable.Range(0, BindingCount)
            .Select(index => new UserProviderConfigurationBinding(index == 0 ? "user" : "user-" + index, "existing", Revision, Fingerprint)).ToArray();
        public OperationResult<bool> CommitProviderBinding(string userId, string scriptId, string expectedFingerprint, string revision, string expectedScriptFingerprint)
        {
            if (expectedFingerprint != Fingerprint || ProviderConfigurationOperations.Fingerprint(scripts.Visible) != expectedScriptFingerprint)
                return OperationResult<bool>.Failure("configuration.user.cas_conflict", "changed", OperationErrorKind.Conflict);
            Revision = revision; return OperationResult<bool>.Ok(true);
        }
    }

    [Fact]
    public async Task TenBindingsShareIdleSourceChecksAndStopObservingAfterDisposal()
    {
        using var fixture = new ProviderWorkerFixture();
        string source = Path.Combine(fixture.Root, "owned-source.json"); File.WriteAllText(source, "before");
        var repository = new Repository { Visible = new() { Id = "existing", Name = "Shared root", RootPath = fixture.Root,
            ExecutionProviderId = "maa-framework", ExecutionProviderConfigId = "profile", ExecutionProviderConfigRevision = "shared" } };
        var users = new UserStore(repository) { BindingCount = 9 };
        var provider = new Provider { SourcePath = source, CommittedSource = "before" };
        var requests = new System.Collections.Concurrent.ConcurrentQueue<ProviderConfigurationSaveRequest>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var synchronization = new ProviderConfigurationSynchronization(repository, new Providers(provider, fixture.Root), requests.Enqueue,
            _ => false, (_, _, _, _) => { }, users);
        try
        {
            while (Volatile.Read(ref provider.Inspections) < 10) await Task.Delay(20, timeout.Token);
            Assert.Equal(10, Volatile.Read(ref provider.Inspections));
            for (int index = 0; index < 5; index++)
                File.WriteAllText(Path.Combine(fixture.Root, "unrelated-worker.log"), index.ToString());
            await Task.Delay(TimeSpan.FromSeconds(62), timeout.Token);
            Assert.Equal(10, Volatile.Read(ref provider.Inspections));
            Assert.Empty(requests);
            File.WriteAllText(source, "after");
            while (requests.Count < 10) await Task.Delay(20, timeout.Token);
            Assert.Equal(20, Volatile.Read(ref provider.Inspections));
            Assert.Equal(10, requests.Select(request => request.UserId).Distinct().Count());
            Assert.All(requests, request => Assert.True(request.SourceOnly));
            Console.WriteLine("source-watch measurement: bindings=10; initialInspections=10; idleSeconds=62; idleInspections=0; changedInspections=10; sourceOnlyRequests=10");
        }
        finally { await synchronization.DisposeAsync(); }
        int stopped = Volatile.Read(ref provider.Inspections);
        File.WriteAllText(source, "after-disposal");
        await Task.Delay(1500, timeout.Token);
        Assert.Equal(stopped, Volatile.Read(ref provider.Inspections));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceChangesCoalesceWaitForIdleAndCommitWithoutReplayingMaaPrograms(bool sourceOnly)
    {
        using var fixture = new ProviderWorkerFixture();
        string source = Path.Combine(fixture.Root, "owned-source.json"); File.WriteAllText(source, "before");
        var repository = new Repository { Visible = new() { Id = "existing", Name = "Owned sync", RootPath = fixture.Root,
            ExecutionProviderId = sourceOnly ? "maa-framework" : "fixture", ExecutionProviderConfigId = "profile", ExecutionProviderConfigRevision = "previous" } };
        var provider = new Provider { SourcePath = source, CommittedSource = "before" }; var events = new RealtimeEventBus();
        var state = new ExecutionStateStore();
        await using var operations = new ProviderConfigurationOperations(repository, new Providers(provider, fixture.Root), Dispatcher(state, events), events,
            Path.Combine(fixture.Root, "sync-journals"), automaticCommit: repository.Commit);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (Volatile.Read(ref provider.Inspections) == 0) await Task.Delay(20, timeout.Token);
        Assert.True(state.TryBeginProviderConfiguration("busy", "other", fixture.Root, [new("writable_root", fixture.Root)], out _));
        File.WriteAllText(source, "after-one"); await Task.Delay(200, timeout.Token); File.WriteAllText(source, "after-two");
        using var noiseStop = new CancellationTokenSource();
        var noise = Task.Run(async () => {
            try
            {
                while (true)
                {
                    noiseStop.Token.ThrowIfCancellationRequested();
                    File.WriteAllText(Path.Combine(fixture.Root, "unrelated-worker.log"), Guid.NewGuid().ToString());
                    await Task.Delay(100, noiseStop.Token);
                }
            }
            catch (OperationCanceledException) when (noiseStop.IsCancellationRequested) { }
        });
        try
        {
        ProviderConfigurationOperationStatus? pending = null;
        while (pending is null)
        {
            pending = operations.Snapshot().FirstOrDefault(item => item.ScriptId == "existing" && item.State == "waiting_for_idle");
            await Task.Delay(20, timeout.Token);
        }
        Assert.Equal("previous", repository.Visible.ExecutionProviderConfigRevision); Assert.Equal(0, provider.Initializations);
        state.EndEditSession("other", "provider-operation:busy");
        var result = await Wait(operations, pending.OperationId);
        Assert.Equal("ready", result.State); Assert.Equal(result.Revision, repository.Visible.ExecutionProviderConfigRevision);
        Assert.True(result.CleanupConfirmed); Assert.Equal(sourceOnly ? 0 : 1, provider.Initializations); Assert.Equal(1, provider.Commits);
        await Task.Delay(1500, timeout.Token); Assert.Equal(sourceOnly ? 0 : 1, provider.Initializations);
        }
        finally { noiseStop.Cancel(); await noise; }
    }

    [Fact]
    public async Task UnknownRecoveryBytesRemainVisibleAndBlockNewInitializationWithoutReplayingPrograms()
    {
        using var fixture = new ProviderWorkerFixture();
        string root = Path.Combine(fixture.Root, "unknown-journal");
        Directory.CreateDirectory(root);
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes("{\"schemaVersion\":99,\"private\":\"preserve bytes\"}");
        string file = Path.Combine(root, "unknown.json");
        File.WriteAllBytes(file, bytes);
        var repository = new Repository(); var provider = new Provider(); var events = new RealtimeEventBus();
        await using var operations = new ProviderConfigurationOperations(repository, new Providers(provider, fixture.Root),
            Dispatcher(new(), events), events, root);
        var issue = Assert.Single(operations.Snapshot());
        Assert.Equal("needs_attention", issue.State); Assert.False(issue.CleanupConfirmed);
        Assert.Equal(issue, operations.Find(issue.OperationId));
        Assert.Throws<InvalidDataException>(() => operations.Start(Request(fixture.Root), repository.Commit));
        Assert.Equal(bytes, File.ReadAllBytes(file));
        Assert.Equal(0, provider.Initializations); Assert.Equal(0, provider.Stages); Assert.Null(repository.Visible);
    }

    [Fact]
    public async Task InitializeCommitsOneVisibleRevisionAndIdempotentRequestDoesNotRepeatWorker()
    {
        using var fixture = new ProviderWorkerFixture();
        var repository = new Repository(); var provider = new Provider(); var events = new RealtimeEventBus();
        var dispatcher = Dispatcher(new(), events);
        string root = Path.Combine(fixture.Root, "journals");
        var request = Request(fixture.Root);
        await using (var operations = new ProviderConfigurationOperations(repository, new Providers(provider, fixture.Root), dispatcher, events, root))
        {
            var first = operations.Start(request, repository.Commit);
            Assert.Null(repository.Visible);
            var terminal = await Wait(operations, first.OperationId);
            Assert.Equal("ready", terminal.State); Assert.True(terminal.CleanupConfirmed);
            Assert.Equal(terminal.Revision, repository.Visible!.ExecutionProviderConfigRevision);
            Assert.Equal(first.ScriptId, repository.Visible.Id);
            Assert.Equal(terminal.State, operations.Start(request, repository.Commit).State);
            Assert.Equal(1, provider.Initializations); Assert.Equal(1, provider.Commits); Assert.Equal(1, provider.Stages);
            Assert.Throws<InvalidDataException>(() => operations.Start(request with { Configuration = new() { ["different"] = true } }, repository.Commit));
        }
        string file = Path.Combine(root, request.OperationId + ".json");
        File.Move(Path.Combine(root, "completed", request.OperationId + ".json"), file);
        var journal = JsonSerializer.Deserialize<ProviderConfigurationJournal>(File.ReadAllText(file), ProviderConfigurationOperations.Json)!;
        Assert.NotNull(journal.Worker);
        File.WriteAllText(file, JsonSerializer.Serialize(journal with { State = "initializing", CleanupConfirmed = false }, ProviderConfigurationOperations.Json));
        await using var recovered = new ProviderConfigurationOperations(repository, new Providers(provider, fixture.Root), dispatcher, events, root);
        Assert.Equal("ready", recovered.Find(request.OperationId)!.State);
        Assert.Equal(1, provider.Initializations);
    }

    [Fact]
    public async Task CompletedReceiptsDoNotExhaustCapacityAndKeepIdempotencyAfterRestart()
    {
        using var fixture = new ProviderWorkerFixture();
        string root = Path.Combine(fixture.Root, "receipt-capacity");
        string completed = Path.Combine(root, "completed"); Directory.CreateDirectory(completed);
        var repository = new Repository(); var provider = new Provider(); var events = new RealtimeEventBus();
        ProviderConfigurationSaveRequest? oldest = null;
        for (int index = 0; index < 260; index++)
        {
            var request = Request(fixture.Root);
            oldest ??= request;
            var script = request.Script.Clone(); script.Id = "historical_" + index;
            var journal = new ProviderConfigurationJournal { OperationId = request.OperationId, Script = script,
                RequestHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request))),
                State = "cancelled", ErrorCode = "configuration.cancelled", CleanupConfirmed = true };
            File.WriteAllText(Path.Combine(completed, request.OperationId + ".json"), JsonSerializer.Serialize(journal, ProviderConfigurationOperations.Json));
        }
        string oldestPath = Path.Combine(completed, oldest!.OperationId + ".json");
        byte[] original = File.ReadAllBytes(oldestPath);
        await using (var operations = new ProviderConfigurationOperations(repository, new Providers(provider, fixture.Root), Dispatcher(new(), events), events, root))
        {
            Assert.Equal(256, operations.Snapshot().Count);
            Assert.Equal("cancelled", operations.Find(oldest.OperationId)!.State);
            Assert.Equal("cancelled", operations.Start(oldest, repository.Commit).State);
            Assert.Throws<InvalidDataException>(() => operations.Start(oldest with { Configuration = new() { ["changed"] = true } }, repository.Commit));
            Assert.Equal(0, provider.Stages);
            var next = Request(fixture.Root);
            Assert.Equal("ready", (await Wait(operations, operations.Start(next, repository.Commit).OperationId)).State);
            Assert.Equal(1, provider.Stages); Assert.Equal(1, provider.Initializations);
            Assert.True(File.Exists(Path.Combine(completed, next.OperationId + ".json")));
        }
        Assert.Equal(original, File.ReadAllBytes(oldestPath));
        await using var recovered = new ProviderConfigurationOperations(repository, new Providers(provider, fixture.Root), Dispatcher(new(), events), events, root);
        Assert.Equal("cancelled", recovered.Start(oldest, repository.Commit).State);
        Assert.Equal(1, provider.Initializations);
    }

    [Fact]
    public async Task MissingAuthenticatedFactsFormalTasksAndConcurrentEditsCannotCommit()
    {
        using var fixture = new ProviderWorkerFixture();
        foreach (string failure in new[] { "omitInitializationFact", "formalTask", "noWorker", "concurrentEdit" })
        {
            var repository = new Repository { Visible = new() { Id = "existing", Name = "Previous", RootPath = fixture.Root,
                ExecutionProviderId = "fixture", ExecutionProviderConfigId = "profile", ExecutionProviderConfigRevision = "previous" } };
            var provider = new Provider(); var events = new RealtimeEventBus();
            if (failure == "concurrentEdit") provider.OnCommit = () => repository.Visible!.Name = "Concurrent edit";
            await using var operations = new ProviderConfigurationOperations(repository, new Providers(provider, fixture.Root), Dispatcher(new(), events), events,
                Path.Combine(fixture.Root, failure));
            var request = new ProviderConfigurationSaveRequest(Guid.NewGuid().ToString("N"), repository.Visible.Clone(), new() { [failure] = true });
            var status = await Wait(operations, operations.Start(request, repository.Commit).OperationId);
            Assert.Equal("needs_attention", status.State); Assert.True(status.CleanupConfirmed);
            Assert.Equal("previous", repository.Visible.ExecutionProviderConfigRevision);
            Assert.Equal(failure == "concurrentEdit" ? 1 : 0, provider.Commits);
        }
    }

    [Fact]
    public async Task CancellationAfterAuthenticatedProgressStopsWorkerAndReleasesResourceLease()
    {
        using var fixture = new ProviderWorkerFixture();
        var repository = new Repository(); var provider = new Provider(); var events = new RealtimeEventBus();
        var state = new ExecutionStateStore(); var dispatcher = Dispatcher(state, events);
        await using var operations = new ProviderConfigurationOperations(repository, new Providers(provider, fixture.Root), dispatcher, events, Path.Combine(fixture.Root, "cancel"));
        var request = Request(fixture.Root) with { Configuration = new() { ["waitAfterLog"] = true } };
        operations.Start(request, repository.Commit);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (operations.Find(request.OperationId)!.Progress["code"]?.GetValue<string>() != "fixture.initialization_wait") await Task.Delay(10, timeout.Token);
        Assert.True(operations.Cancel(request.OperationId));
        var result = await Wait(operations, request.OperationId);
        Assert.Equal("cancelled", result.State); Assert.True(result.CleanupConfirmed);
        Assert.Null(repository.Visible); Assert.Equal(0, provider.Commits);
        Assert.True(state.TryBeginProviderConfiguration("next", "next", fixture.Root, [new("writable_root", fixture.Root), new("desktop_input", "current_session")], out _));
        state.EndEditSession("next", "provider-operation:next");
    }

    [Fact]
    public void ConfigurationLeaseUsesDesktopAndExactAdbResourcesAcrossDifferentRoots()
    {
        using var fixture = new ProviderWorkerFixture();
        using var second = new ProviderWorkerFixture();
        string other = second.Root;
        var state = new ExecutionStateStore();
        Assert.True(state.TryBeginProviderConfiguration("one", "one", fixture.Root, [new("writable_root", fixture.Root), new("desktop_input", "current_session")], out _));
        Assert.False(state.TryBeginProviderConfiguration("two", "two", other, [new("writable_root", other), new("desktop_input", "current_session")], out _));
        state.EndEditSession("one", "provider-operation:one");
        Assert.True(state.TryBeginProviderConfiguration("one", "one", fixture.Root, [new("writable_root", fixture.Root), new("adb_endpoint", "127.0.0.1:5555")], out _));
        Assert.False(state.TryBeginProviderConfiguration("two", "two", other, [new("writable_root", other), new("adb_endpoint", "127.0.0.1:5555")], out _));
        state.EndEditSession("one", "provider-operation:one");
        Assert.True(state.TryBeginProviderConfiguration("switch", "switch", other, [new("writable_root", other)], out _, fixture.Root));
        Assert.False(state.TryBeginProviderConfiguration("old-root", "old-root", fixture.Root, [new("writable_root", fixture.Root)], out _));
        Assert.False(state.TryBeginProviderConfiguration("new-root", "new-root", other, [new("writable_root", other)], out _));
        state.EndEditSession("switch", "provider-operation:switch");
    }

    private static ProviderConfigurationSaveRequest Request(string root) => new(Guid.NewGuid().ToString("N"),
        new() { Id = "", Name = "Owned save", RootPath = root, ExecutionProviderId = "fixture", ExecutionProviderConfigId = "profile" }, new());
    private static ExecutionDispatcher Dispatcher(ExecutionStateStore state, RealtimeEventBus events) => new(state, null!, null!, null!, events);
    private static async Task<ProviderConfigurationOperationStatus> Wait(ProviderConfigurationOperations operations, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var status = operations.Find(id)!;
            if (ProviderConfigurationOperations.Terminal(status.State)) return status;
            await Task.Delay(10, timeout.Token);
        }
    }
    private sealed class Repository : IScriptRepository
    {
        internal ScriptInstance? Visible;
        public ScriptInstance? FindById(string id) => Visible?.Id == id ? Visible.Clone() : null;
        public IReadOnlyList<ScriptInstance> Snapshot() => Visible is null ? [] : [Visible.Clone()];
        internal OperationResult<ScriptInstance> Commit(ScriptInstance script, string expected)
        {
            if (ProviderConfigurationOperations.Fingerprint(Visible) != expected)
                return OperationResult<ScriptInstance>.Failure("configuration.script.cas_conflict", "changed", OperationErrorKind.Conflict);
            Visible = script.Clone(); return OperationResult<ScriptInstance>.Ok(Visible.Clone());
        }
    }
    private sealed class Providers(Provider provider, string root) : IPluginExecutionProviderResolver
    {
        public ExecutionProviderDescriptor? ResolveExecutionProvider(string id) => new(id, "fixture", root, provider);
    }
    private sealed class Provider : IPluginExecutionProvider, IPluginProviderConfigurationLifecycle, IPluginProviderConfigurationSynchronization
    {
        internal int Stages, Initializations, Commits;
        internal PluginProviderConfigurationRequest? LastStage;
        internal Action? OnCommit;
        internal string? SourcePath;
        internal string CommittedSource = "";
        internal int Inspections;
        public ValueTask<PluginProviderConfigurationSourceState> InspectSourceAsync(PluginProviderConfigurationRequest r, CancellationToken t)
        {
            Interlocked.Increment(ref Inspections);
            string current = File.ReadAllText(SourcePath!);
            return ValueTask.FromResult(new PluginProviderConfigurationSourceState(current == CommittedSource ? "current" : "changed", current, "",
                [Path.GetFileName(SourcePath!)], []));
        }
        public string Id => "fixture";
        public ValueTask<PluginProviderInspection> InspectAsync(PluginProviderInspectRequest r, CancellationToken t) => throw new NotSupportedException();
        public ValueTask<PluginProviderPlan> PrepareAsync(PluginProviderPrepareRequest r, CancellationToken t) => throw new NotSupportedException();
        public Task<PluginProviderRunResult> RunAsync(PluginProviderRunContext c, CancellationToken t) => throw new NotSupportedException();
        public ValueTask<PluginProviderConfigurationDiscovery> DiscoverAsync(PluginProviderConfigurationRequest r, CancellationToken t) => throw new NotSupportedException();
        public ValueTask<PluginProviderConfigurationCandidate> StageAsync(PluginProviderConfigurationRequest r, CancellationToken t)
        {
            LastStage = r;
            Stages++;
            return ValueTask.FromResult(new PluginProviderConfigurationCandidate(r.OperationId, r.ProfileId, Guid.NewGuid().ToString("N"), r.ExpectedRevision,
                "fixture-fingerprint", [new("writable_root", r.PackageRoot), new("desktop_input", "current_session")],
                new() { ["root"] = r.PackageRoot, ["configuration"] = r.Configuration.DeepClone() }, new()));
        }
        public async Task<PluginProviderConfigurationInitialization> InitializeAsync(PluginProviderConfigurationContext c, CancellationToken t)
        {
            Initializations++;
            var configuration = c.Candidate.PrivateCandidate["configuration"]!.AsObject();
            if (configuration["noWorker"]?.GetValue<bool>() == true) return new(true, true, new(), []);
            var input = configuration.DeepClone().AsObject();
            input["mode"] = "initialize_only"; input["operationId"] = c.Candidate.OperationId;
            input["revision"] = c.Candidate.Revision; input["fingerprint"] = c.Candidate.AuthorizationFingerprint;
            var result = await c.Worker.RunAsync(new("NexusPipeline.TestProviderWorker.exe", [], c.Candidate.PrivateCandidate["root"]!.GetValue<string>(), input), c.PublishEvent, t);
            return new(result.ExitKind == "completed", result.CleanupConfirmed, new(), []);
        }
        public ValueTask CommitAsync(PluginProviderConfigurationCandidate c, PluginProviderConfigurationInitialization i, CancellationToken t)
        { Commits++; OnCommit?.Invoke(); if (SourcePath is not null) CommittedSource = File.ReadAllText(SourcePath); return ValueTask.CompletedTask; }
        public ValueTask AbortAsync(PluginProviderConfigurationCandidate c, CancellationToken t) => ValueTask.CompletedTask;
    }
}
