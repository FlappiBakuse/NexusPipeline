using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Media;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class PreviewDomainTests
{
    [Fact(DisplayName = "Closing One Preview Cancels And Collects Its Real Capture Without Waiting For Another Grant")]
    public async Task ClosesSingleGrant()
    {
        var fixture = new PcStageFixture(hooks: false, mainWait: true, nativeWindow: true);
        await fixture.ExecuteAsync(async () =>
        {
            var state = fixture.State;
            var context = new RunMediaContext(fixture.Source.Instance.InstanceId, Guid.Parse(state.Attempt.RunId), Guid.Parse(state.Attempt.AttemptId));
            var leftSource = new HoldReturnedFrame(fixture.LiveMedia); var rightSource = new HoldReturnedFrame(fixture.LiveMedia);
            var activation = new ExtensionActivation("preview-domain-fixture");
            using var leftRegistration = fixture.LiveMedia.Previews.Register(activation, "left", leftSource);
            using var rightRegistration = fixture.LiveMedia.Previews.Register(activation, "right", rightSource); activation.Publish();
            var running = fixture.Consumer.RunNextAsync();
            try
            {
                await fixture.AwaitMainAsync();
                await using var left = await OpenAsync(fixture, context, "left");
                await using var right = await OpenAsync(fixture, context, "right");
                var leftCapture = left.CaptureAsync().AsTask(); var rightCapture = right.CaptureAsync().AsTask();
                await Task.WhenAll(leftSource.Ready.Task, rightSource.Ready.Task).WaitAsync(TimeSpan.FromSeconds(20));
                await left.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leftCapture);
                Assert.False(rightCapture.IsCompleted);
                var domain = Assert.Single(Domains(fixture.State)); Assert.False(domain.Closed);
                Assert.True(OwnedPreviewJob.ObserveStopped(domain.Source.JobName, domain.Source.Primary.SessionId));
                await right.DisposeAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rightCapture);
            }
            finally
            {
                File.WriteAllText(fixture.HookMarker + ".main.release", "release");
                await running.WaitAsync(TimeSpan.FromSeconds(45));
            }
        });
    }

    [Fact(DisplayName = "Cold Open Requires The Original Sql Creator To Stop And Does Not Replace Missing Primary Closures")]
    public async Task RequiresStoppedCreator()
    {
        using var fixture = new PcCrashFixture(nativeWindow: true, livePreview: true);
        await fixture.StartAsync("LivePreviewOpen");
        var source = fixture.Source;
        var before = AttemptWriterState.Read(source.Instance, source.Database, new(), fixture.Parent);
        var domain = Assert.Single(Domains(before)); Assert.False(domain.Closed);
        Assert.Throws<IOException>(() => PreviewDomainEvidence.RequireColdStopped(domain.Source));
        using (var connection = DatabaseConnections.Open(source.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Automation | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content))
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            PreviewDomainEvidence.RequireSourceInside(connection, transaction, domain.Source);
            Assert.Throws<IOException>(() => PreviewDomainEvidence.RequireSourceInside(connection, transaction,
                domain.Source with { Creator = domain.Source.Creator with { ProcessId = domain.Source.Creator.ProcessId + 1 } }));
        }
        var installed = File.ReadAllBytes(source.Configuration);
        await fixture.KillOwnerAsync(); await fixture.RequireStoppedAsync();
        PreviewDomainEvidence.RequireColdStopped(domain.Source);
        PreviewDomainEvidence.RequireStopped(source.Database, before);
        var primary = Assert.Single(before.Jobs);
        Assert.Null(new JobClosureStore(source.Instance, source.Database, new()).ReadForExecutionEvidence(
            fixture.Parent, primary.WriterId, primary.JobName, primary.SessionId));
        var configuration = Assert.Single(before.Attempt.ConfigurationPlans).Plan;
        var child = new RecoveryJournalRegistry(source.Database).ReadSnapshot().Journals.Single(item =>
            item.PlanPath == configuration.RelativePath && item.PlanDigest == configuration.Sha256);
        Assert.Throws<IOException>(() => StepWritersEvidence.RequireConfigurationStopped(source.Instance,
            source.Database, new(), child.JournalId));
        Assert.Equal(installed, File.ReadAllBytes(source.Configuration));
        Assert.Null(new AttemptReceiptStore(source.Database).Read(fixture.Attempt));
    }

    [Fact(DisplayName = "Closing And Reopening The Ui Keeps One Actual Step Domain And Its Two Finite Journal Events")]
    public async Task ReusesStepDomain()
    {
        var fixture = new PcStageFixture(configuration: true, distinctConfiguration: true,
            hooks: false, mainWait: true, nativeWindow: true);
        await fixture.ExecuteAsync(async () =>
        {
            var initial = fixture.State;
            var context = new RunMediaContext(fixture.Source.Instance.InstanceId,
                Guid.Parse(initial.Attempt.RunId), Guid.Parse(initial.Attempt.AttemptId));
            var activation = new ExtensionActivation("preview-domain-fixture");
            using var registration = fixture.LiveMedia.Previews.Register(activation, "window", fixture.LiveMedia); activation.Publish();
            var original = File.ReadAllBytes(fixture.Source.Configuration);
            var running = fixture.Consumer.RunNextAsync();
            try
            {
                await fixture.AwaitMainAsync();
                await using var preview = await OpenAsync(fixture, context);
                Assert.Empty(Domains(fixture.State));
                Assert.True((await preview.CaptureAsync()).Width > 0);
                var open = Assert.Single(Domains(fixture.State)); Assert.False(open.Closed);
                Assert.Equal(1, DomainParticipants(fixture.State));
                RequireBound(fixture, open.Source);
                Assert.Throws<IOException>(() => PreviewDomainEvidence.RequireColdStopped(open.Source));
                Assert.True(OwnedPreviewJob.ObserveStopped(open.Source.JobName, open.Source.Primary.SessionId));
                using (var connection = DatabaseConnections.Open(fixture.Source.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content))
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT COUNT(*) FROM main.journal_writer_jobs WHERE journal_id=$parent OR job_name=$preview;";
                    command.Parameters.AddWithValue("$parent", open.Source.Primary.ParentJournalId.ToString("D"));
                    command.Parameters.AddWithValue("$preview", open.Source.JobName);
                    Assert.Equal(fixture.State.Jobs.Length, (long)command.ExecuteScalar()!);
                }
                await preview.DisposeAsync();
                Assert.False(Assert.Single(Domains(fixture.State)).Closed);
                await using (var reopened = await OpenAsync(fixture, context))
                    Assert.True((await reopened.CaptureAsync()).SizeBytes > 0);
                Assert.Equal(open.Source, Assert.Single(Domains(fixture.State)).Source);
                Assert.Equal(1, DomainParticipants(fixture.State));
                RequireTamperRejected(fixture, open.Source);
                File.WriteAllText(fixture.HookMarker + ".main.release", "release");
                var receipt = Assert.IsType<AttemptReceipt>(await running.WaitAsync(TimeSpan.FromSeconds(45)));
                Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
                var final = fixture.State; var closed = Assert.Single(Domains(final));
                Assert.True(closed.Closed); Assert.Equal(open.Source, closed.Source); Assert.Equal(2, DomainParticipants(final));
                var events = final.Latest.Root.GetProperty("participants").EnumerateArray()
                    .Where(PreviewDomainCodec.IsParticipant).Select(PreviewDomainCodec.Decode).ToArray();
                Assert.True(events[1].TotalProcesses >= 2);
                Assert.True(OwnedPreviewJob.ObserveStopped(closed.Source.JobName, closed.Source.Primary.SessionId));
                PreviewDomainEvidence.RequireStopped(fixture.Source.Database, final);
                var imageMetadata = new ImageMetadataRead(fixture.Source.Instance, fixture.Source.Database, new()).Read(final.Journal);
                Assert.Equal(final.Attempt.AttemptId, imageMetadata.Plan.AttemptId);
                Assert.NotEmpty(imageMetadata.Captures);
                var history = new JournalStore(fixture.Source.Instance, new()).ReadAllRevisions(fixture.Accepted.JournalId);
                PreviewDomainCodec.RequireHistory(history, final.Total, final.Attempt.ExecutionPlan.Sha256);
                var changed = JsonNode.Parse(final.Latest.Export())!.AsObject();
                foreach (var participant in changed["participants"]!.AsArray().OfType<JsonObject>()
                    .Where(value => value["identity"]?["format"]?.GetValue<string>() == PreviewDomainCodec.Format))
                    participant["identity"]!["source"]!["creator"]!["processId"] = closed.Source.Creator.ProcessId + 1;
                var changedHistory = history.ToArray();
                changedHistory[^1] = JournalDocument.Read(JsonSerializer.SerializeToUtf8Bytes(changed), new());
                Assert.Throws<IOException>(() => PreviewDomainCodec.RequireHistory(changedHistory, final.Total,
                    final.Attempt.ExecutionPlan.Sha256));
                Assert.Equal(original, File.ReadAllBytes(fixture.Source.Configuration));
                Assert.Equal(0, fixture.Consumer.PendingReconciliationCount);
            }
            finally
            {
                File.WriteAllText(fixture.HookMarker + ".main.release", "release");
                await running.WaitAsync(TimeSpan.FromSeconds(45));
            }
        });
    }

    private static void RequireTamperRejected(PcStageFixture fixture, PreviewDomainSource source)
    {
        foreach (var changed in new[]
        {
            source with { Creator = source.Creator with { ProcessId = source.Creator.ProcessId + 1 } },
            source with { MainRoot = source.MainRoot with { CreatedAtUtc = source.MainRoot.CreatedAtUtc.AddTicks(1) } },
            source with { Primary = source.Primary with { WriterId = Guid.NewGuid() } },
            source with { Primary = source.Primary with { SessionId = source.Primary.SessionId + 1 } }
        }) Assert.Throws<IOException>(() => RequireBound(fixture, changed));
        var item = new PreviewDomainIntent(PreviewDomainCodec.Format, 1, source, "open", 10, null);
        foreach (var changed in new[]
        {
            source with { JobName = "Local\\NexusWorker-" + Guid.NewGuid().ToString("N") },
            source with { Flags = 0x2000 }, source with { ActiveLimit = 4096 }, source with { MemoryLimit = 0 },
            source with { TotalPlanSha256 = new string('0', 64) }
        })
        {
            var participant = PreviewDomainCodec.Participant(item with { Source = changed }, fixture.State.Total.Steps[0].ScriptId);
            using var document = JsonDocument.Parse(participant.ToJsonString());
            Assert.Throws<IOException>(() => PreviewDomainCodec.Decode(document.RootElement));
        }
        var state = fixture.State;
        var closed = PreviewDomainCodec.Participant(item with { Stage = "closed", BeforeRevision = state.Latest.Revision,
            TotalProcesses = 2 }, state.Total.Steps[0].ScriptId);
        var next = JsonNode.Parse(state.Latest.Export())!.AsObject(); next["revision"] = state.Latest.Revision + 1;
        next["participants"]!.AsArray().Add(closed);
        var forged = JournalDocument.Read(JsonSerializer.SerializeToUtf8Bytes(next), new());
        using var connection = DatabaseConnections.Open(fixture.Source.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.All);
        using var transaction = connection.BeginTransaction(deferred: true);
        Assert.Throws<IOException>(() => PreviewDomainProjection.RequireInside(connection, transaction, state,
            state.Latest, forged, null));
    }

    private static void RequireBound(PcStageFixture fixture, PreviewDomainSource source)
    {
        using var connection = DatabaseConnections.Open(fixture.Source.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.All);
        using var transaction = connection.BeginTransaction(deferred: true);
        PreviewDomainEvidence.RequireSourceInside(connection, transaction, source); transaction.Commit();
    }
    private static System.Collections.Immutable.ImmutableArray<PreviewDomainState> Domains(AttemptWriterState state) =>
        PreviewDomainCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256);
    private static int DomainParticipants(AttemptWriterState state) =>
        state.Latest.Root.GetProperty("participants").EnumerateArray().Count(PreviewDomainCodec.IsParticipant);
    private sealed class HoldReturnedFrame(IMediaFrameSource actual) : IMediaFrameSource
    {
        internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<PreviewImage> CaptureAsync(MediaCaptureRequest request, CancellationToken cancellationToken)
        {
            var image = await actual.CaptureAsync(request, cancellationToken);
            Ready.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return image;
        }
    }

    private static async Task<ExecutionLivePreview> OpenAsync(PcStageFixture fixture, RunMediaContext context, string provider = "window")
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            try { return fixture.LiveMedia.OpenPreview(PcStageFixture.Subject, context, "preview-domain-fixture", provider); }
            catch (UnauthorizedAccessException) when (clock.Elapsed < TimeSpan.FromSeconds(10)) { await Task.Delay(20); }
        }
    }
}
