using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Maintenance;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class MaintenanceBinaryTests
{
    [Fact(DisplayName = "An Actual Candidate Exit Before Its Receipt Restores The Original Payload Only After Its Job Stops")]
    public Task RestoresFailedCandidate() => VerifyRestorationAsync();

    [Fact]
    public async Task RestoresPublishedPayload()
    {
        using var inputs = new PublishedUpdateInputs();
        await VerifyRestorationAsync(inputs);
        inputs.VerifyUnchanged();
    }

    [Fact(DisplayName = "Current Backend Assemblies Restore Within An Isolated Derived Full Runtime Payload")]
    public async Task RestoresDerivedPayload()
    {
        using var inputs = new PublishedUpdateInputs(deriveRuntime: true);
        await VerifyRestorationAsync(inputs);
        inputs.VerifyUnchanged();
    }

    private static async Task VerifyRestorationAsync(PublishedUpdateInputs? published = null)
    {
        using var origin = MaintenanceStartupOrigin.CaptureUnjobbedProcess();
        using var fixture = new MaintenanceBinaryFixture(failCandidateStartup: true, published: published);
        // Restoration verifies both complete payload trees and reopens the original Host under its retained Job.
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var old = fixture.Start();
        Assert.False(string.IsNullOrEmpty(await old.StandardOutput.ReadLineAsync(deadline.Token)));
        await fixture.AwaitSchedulingAsync(deadline.Token);
        using var client = new HttpClient { BaseAddress = fixture.Address, Timeout = TimeSpan.FromSeconds(30) };
        using var response = await client.PostAsync($"control/v1/maintenance/operations/{fixture.Plan.OperationId:D}/start", null, deadline.Token);
        Assert.Equal(202, (int)response.StatusCode);
        using var helper = await fixture.AwaitHelperAsync(deadline.Token);
        await old.WaitForExitAsync(deadline.Token);
        Assert.Equal(0, old.ExitCode);
        await helper.WaitForExitAsync(deadline.Token);
        Assert.Equal(0, helper.ExitCode);
        using var successor = fixture.ObserveSuccessor(old.Id);
        using var restoredClient = new HttpClient { BaseAddress = fixture.Address, Timeout = TimeSpan.FromSeconds(2) };
        HttpResponseMessage? restoredReady = null;
        while (restoredReady is null)
        {
            Assert.False(successor.HasExited, "Restored Host exited before HTTP readiness");
            deadline.Token.ThrowIfCancellationRequested();
            try { restoredReady = await restoredClient.GetAsync("control/v1/capabilities", deadline.Token); }
            catch (HttpRequestException) { await Task.Delay(100, deadline.Token); }
            catch (TaskCanceledException) when (!deadline.IsCancellationRequested) { }
        }
        using (restoredReady) Assert.Equal(200, (int)restoredReady.StatusCode);
        using var restoredOperation = await restoredClient.GetAsync($"control/v1/maintenance/operations/{fixture.Plan.OperationId:D}", deadline.Token);
        Assert.Equal(200, (int)restoredOperation.StatusCode);
        using var restoredOutcome = JsonDocument.Parse(await restoredOperation.Content.ReadAsStringAsync(deadline.Token));
        Assert.Equal("originalRestored", restoredOutcome.RootElement.GetProperty("outcome").GetString());
        using (var connection = DatabaseConnections.Open(fixture.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.System))
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            var row = MaintenanceOperationRows.ReadOwned(connection, transaction, fixture.Plan.OperationId, fixture.Subject);
            Assert.NotNull(row); Assert.Equal(HostUpdateState.Completed, row.State);
            Assert.Equal("maintenance_original_restored", row.TerminalCode); transaction.Commit();
        }
        if (published is null) Assert.Equal("original", File.ReadAllText(Path.Combine(fixture.Plan.TargetPath, "README.md")));
        else published.VerifyPayload(fixture.Plan.TargetPath);
        var journal = new RecoveryJournalRegistry(fixture.Database).ReadJournal(fixture.Plan.JournalId);
        Assert.Equal(NexusPipeline.Core.Configuration.JournalPhase.Committed, journal.Phase);
        using var sql = DatabaseConnections.Open(fixture.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.System);
        using var command = sql.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM main.journal_writers WHERE journal_id=$journal;";
        command.Parameters.AddWithValue("$journal", fixture.Plan.JournalId.ToString("D"));
        Assert.Equal(4L, command.ExecuteScalar());
        command.CommandText = "SELECT COUNT(*) FROM main.journal_writer_jobs WHERE journal_id=$journal;";
        Assert.Equal(3L, command.ExecuteScalar());
        command.CommandText = "SELECT process_id FROM main.journal_writers WHERE journal_id=$journal AND image_path=$path AND process_id<>$old ORDER BY created_file_time DESC LIMIT 1;";
        command.Parameters.AddWithValue("$path", fixture.HostPath); command.Parameters.AddWithValue("$old", old.Id);
        var restoredId = Convert.ToInt32(command.ExecuteScalar());
        Assert.NotEqual(old.Id, restoredId); fixture.TrackCandidate(restoredId);
        Assert.Equal(fixture.HostPath, ProcessIdentityReader.Read(restoredId).ImagePath);
    }

    [Fact(DisplayName = "Actual Host Pending Start Transfers To The Helper And Commits The Verified Candidate")]
    public Task CommitsPreparedCandidate() => VerifyUpdateAsync(false);

    [Fact(DisplayName = "A Pending Next Startup Update Runs Before Scheduled Work And Commits The Actual Candidate")]
    public Task ConsumesStartupUpdate() => VerifyUpdateAsync(true);

    [Fact]
    public Task ConsumesDeferredStartup() => VerifyUpdateAsync(true, startupFailure: true);

    [Fact(DisplayName = "Actual Owned Electron Maintenance Restarts The Candidate With The Frozen Visible Settings Window")]
    public Task RestartsOwnedDesktop() => VerifyUpdateAsync(false, true);

    [Fact]
    public async Task AppliesPublishedArchive()
    {
        using var inputs = new PublishedUpdateInputs();
        await VerifyUpdateAsync(false, published: inputs);
        inputs.VerifyUnchanged();
    }

    private static async Task VerifyUpdateAsync(bool nextStartup, bool desktop = false, bool startupFailure = false,
        PublishedUpdateInputs? published = null)
    {
        using var origin = MaintenanceStartupOrigin.CaptureUnjobbedProcess();
        using var fixture = new MaintenanceBinaryFixture(timing: nextStartup ? HostUpdateTiming.NextStartup : HostUpdateTiming.Now,
            desktop: desktop, acceptPending: !startupFailure, hostVersion: startupFailure ? "0.18.1" : "0.18.0", published: published);
        if (startupFailure) await StartupDeferral.PrepareAsync(fixture);
        // Published payload verification reads the complete archive and both trees; admission and old-root exit have separate deadlines.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(published is not null ? 900 : desktop ? 300 : 90));
        var transferStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        var old = fixture.Start();
        var line = await old.StandardOutput.ReadLineAsync(deadline.Token);
        Assert.False(string.IsNullOrEmpty(line), old.HasExited ? await fixture.HostErrors : "Host not ready");
        using var ready = JsonDocument.Parse(line!);
        Assert.Equal(old.Id, ready.RootElement.GetProperty("processId").GetInt32());
        using var oldDesktop = desktop ? await MaintenanceDesktopObservation.WaitAsync(fixture, old.Id, false, deadline.Token) : null;
        using var client = new HttpClient { BaseAddress = fixture.Address, Timeout = TimeSpan.FromSeconds(published is null ? 20 : 30) };
        var path = $"control/v1/maintenance/operations/{fixture.Plan.OperationId:D}";
        if (!nextStartup)
        {
            await fixture.AwaitSchedulingAsync(deadline.Token);
            using var before = await client.GetAsync(path, deadline.Token);
            Assert.Equal(200, (int)before.StatusCode);
            var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            using var started = await client.PostAsync(path + "/start", null, deadline.Token);
            var startMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            if (published is not null) File.WriteAllBytes(Path.Combine(fixture.Root, "published-start-latency.json"),
                JsonSerializer.SerializeToUtf8Bytes(new { milliseconds = startMilliseconds,
                    browserDeadlineMilliseconds = 30000, scope = "Full original archive handoff latency." }));
            Assert.True((int)started.StatusCode == 202,
                "Maintenance start: " + (int)started.StatusCode + " " + await started.Content.ReadAsStringAsync(deadline.Token));
            if (published is not null) Assert.True(startMilliseconds < 30000, "Start exceeds browser deadline");
        }
        using var helper = await fixture.AwaitHelperAsync(deadline.Token);
        var oldExitStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        using (var oldExit = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
        {
            // Complete payload handoff verifies the original archive and protected trees before exiting.
            oldExit.CancelAfter(TimeSpan.FromSeconds(published is null ? 40 : 900));
            await old.WaitForExitAsync(oldExit.Token);
        }
        if (published is not null) File.WriteAllBytes(Path.Combine(fixture.Root, "published-old-exit-latency.json"),
            JsonSerializer.SerializeToUtf8Bytes(new { milliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(oldExitStarted).TotalMilliseconds,
                scope = "Old Host exit after helper observation within the original full-payload test deadline; no desktop startup budget claim." }));
        Assert.Equal(0, old.ExitCode);
        if (oldDesktop is not null) await oldDesktop.Process.WaitForExitAsync(deadline.Token);
        HttpResponseMessage? response = null;
        DateTimeOffset? helperExitedAt = null;
        using var candidateClient = new HttpClient { BaseAddress = fixture.Address, Timeout = TimeSpan.FromSeconds(2) };
        // Reading the live SQLite group here can change SHM while maintenance freezes it for admission.
        while (response is null)
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (helper.HasExited)
            {
                helperExitedAt ??= DateTimeOffset.UtcNow;
                Assert.True(helper.ExitCode == 0, "Maintenance exit stage: " + helper.ExitCode);
                // A complete desktop payload is committed and verified after the helper exits.
                if (!desktop && published is null) Assert.True(DateTimeOffset.UtcNow - helperExitedAt < TimeSpan.FromSeconds(10), "Helper exited 0 without candidate readiness");
            }
            try { response = await candidateClient.GetAsync("control/v1/capabilities", deadline.Token); }
            catch (HttpRequestException) { await Task.Delay(100, deadline.Token); }
            catch (TaskCanceledException) when (!deadline.IsCancellationRequested) { }
        }
        using (response) Assert.Equal(200, (int)response.StatusCode);
        if (published is not null) File.WriteAllBytes(Path.Combine(fixture.Root, "published-ready-latency.json"),
            JsonSerializer.SerializeToUtf8Bytes(new { milliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(transferStarted).TotalMilliseconds,
                helperExitToHttpMilliseconds = helperExitedAt is { } exited ? (DateTimeOffset.UtcNow - exited).TotalMilliseconds : (double?)null,
                scope = "Actual full payload transfer through public HTTP readiness; functional budget only, no desktop startup or resource-budget claim." }));
        if (desktop) { await helper.WaitForExitAsync(deadline.Token); Assert.Equal(0, helper.ExitCode); }
        using var completed = await candidateClient.GetAsync(path, deadline.Token);
        Assert.Equal(200, (int)completed.StatusCode);
        using var operation = JsonDocument.Parse(await completed.Content.ReadAsStringAsync(deadline.Token));
        Assert.Equal("updated", operation.RootElement.GetProperty("outcome").GetString());
        MaintenanceOperationRow? row;
        using (var connection = DatabaseConnections.Open(fixture.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Automation | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content))
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            row = MaintenanceOperationRows.ReadOwned(connection, transaction, fixture.Plan.OperationId, fixture.Subject);
            transaction.Commit();
        }
        Assert.NotNull(row);
        Assert.Equal(HostUpdateState.Completed, row.State);
        Assert.Equal("candidate_started", row.TerminalCode);
        if (published is null) Assert.Equal("candidate", File.ReadAllText(Path.Combine(fixture.Plan.TargetPath, "README.md")));
        else published.VerifyPayload(fixture.Plan.TargetPath);
        using (var connection = DatabaseConnections.Open(fixture.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Automation | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT process_id FROM main.journal_writers WHERE journal_id=$journal AND image_path=$path AND process_id<>$old;";
            command.Parameters.AddWithValue("$journal", fixture.Plan.JournalId.ToString("D"));
            command.Parameters.AddWithValue("$path", fixture.HostPath); command.Parameters.AddWithValue("$old", old.Id);
            var candidateId = Convert.ToInt32(command.ExecuteScalar());
            Assert.NotEqual(old.Id, candidateId); fixture.TrackCandidate(candidateId);
            if (oldDesktop is not null) await VerifySuccessorAsync(fixture, oldDesktop, candidateId, deadline.Token);
        }
        var journal = new RecoveryJournalRegistry(fixture.Database).ReadJournal(fixture.Plan.JournalId);
        Assert.Equal(NexusPipeline.Core.Configuration.JournalPhase.Committed, journal.Phase);
    }

    private static async Task VerifySuccessorAsync(MaintenanceBinaryFixture fixture, MaintenanceDesktopObservation old,
        int candidateId, CancellationToken cancellation)
    {
        using var successor = await MaintenanceDesktopObservation.WaitAsync(fixture, candidateId, true, cancellation);
        Assert.NotEqual(old.Identity.ProcessId, successor.Identity.ProcessId);
        Assert.True(old.Process.HasExited);
        var source = new UpdateSourceCodec(fixture.Instance, new()).ReadMetadata(fixture.Plan.MaintenanceSource!);
        Assert.Equal(new UpdateWindowIntent(UpdateWindowMode.Desktop, true, "/settings"), source.Window);
        File.WriteAllBytes(Path.Combine(fixture.Root, "desktop-handoff.json"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            oldDesktop = new { old.Identity, old.Window, old.Bootstrap },
            candidateDesktop = new { successor.Identity, successor.Window, successor.Bootstrap }, source.Window,
            scope = "Real Electron windows are shown only after the source bridge verifies its signed session; old owned family stopped before replacement."
        }));
    }

}
