using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Scheduling;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class InstalledWorkTests
{
    [Theory(DisplayName = "An Actual Installed SDK Handler Runs A Manual Only Task Under Frozen Host Identity And Survives Its Accepting Frontend Closing")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunsInstalledTask(bool hold)
    {
        using var execution = new ExecutionAdmissionFixture();
        using var catalog = execution.CreateCatalog();
        var source = execution.Source;
        var schedules = new PersistentScheduleService(source.Instance, source.Database, source.Lease, execution.Resources,
            execution.Lifecycle, execution.Index, execution.Service);
        using var installed = new InstallRuntimeFixture(managedMode: "frontend", catalog: catalog,
            portDependencies: new(schedules, null, null, null, null, null),
            managedManifestEdit: manifest => manifest["capabilities"]!.AsArray().Add("schedule.register"));
        var taskId = Guid.NewGuid();
        File.WriteAllBytes(Path.Combine(installed.ActivationDirectory, "manual-task.json"), JsonSerializer.SerializeToUtf8Bytes(new { TaskId = taskId, Revision = 7, Hold = hold }));
        var frontend = await FrontendInstalledTests.OpenAsync(installed);
        var operation = Guid.NewGuid(); var clientId = Guid.NewGuid();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { taskId, revision = 7, operationId = Guid.NewGuid(), runId = Guid.NewGuid(), localDate = "2099-01-01" });
        try
        {
            var clockReply = await frontend.InvokeAsync("manual-clock", null, "remote:work", false, "{}"u8.ToArray(), () => { }, default);
            using var clock = JsonDocument.Parse(clockReply.Content);
            Assert.Equal(TimeProvider.System.LocalTimeZone.Id, clock.RootElement.GetProperty("TimeZoneId").GetString());
            Assert.InRange(clock.RootElement.GetProperty("UtcNow").GetDateTimeOffset(), DateTimeOffset.UtcNow.AddSeconds(-5), DateTimeOffset.UtcNow.AddSeconds(5));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => frontend.InvokeAsync("manual-run", Guid.NewGuid(), "remote:work", false,
                payload, () => { }, default).AsTask());
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => frontend.InvokeAsync("manual-current", null, "remote:work", false,
                payload, () => { }, default, clientId: clientId).AsTask());
            using var clientLifetime = new CancellationTokenSource();
            var reply = await frontend.InvokeAsync("manual-run", operation, "remote:work", false, payload, () => { }, default,
                clientId: clientId, clientLifetime: clientLifetime.Token);
            using var receipt = JsonDocument.Parse(reply.Content);
            Assert.True(receipt.RootElement.GetProperty("IsNew").GetBoolean());
            clientLifetime.Cancel();
            var enteredPath = Path.Combine(installed.ActivationDirectory, "manual-entered.json");
            await WaitFileAsync(enteredPath);
            using var entered = JsonDocument.Parse(await File.ReadAllBytesAsync(enteredPath));
            var state = ReadState(installed);
            Assert.Equal(operation, state.Source.Document.OperationId);
            Assert.Equal(clientId, state.Source.Document.ClientId);
            Assert.Equal(taskId, state.Source.Document.Task.TaskId); Assert.Empty(state.Source.Document.Task.ScheduledTaskIds);
            Assert.Equal(receipt.RootElement.GetProperty("RunId").GetGuid(), entered.RootElement.GetProperty("RunId").GetGuid());
            Assert.Equal(state.Source.Document.Invocation.AttemptId, entered.RootElement.GetProperty("AttemptId").GetGuid());
            Assert.Equal(taskId, entered.RootElement.GetProperty("TaskId").GetGuid());
            Assert.Equal(state.Source.Document.LocalDate.ToString("yyyy-MM-dd"), entered.RootElement.GetProperty("LocalDate").GetString());
            Assert.Equal(state.Source.Document.LocalTimeZoneId, entered.RootElement.GetProperty("TimeZoneId").GetString());
            Assert.NotEqual("2099-01-01", entered.RootElement.GetProperty("LocalDate").GetString());
            Assert.Equal(7, entered.RootElement.GetProperty("TaskRevision").GetInt64());
            if (hold)
            {
                Assert.Null(state.Result);
                var quiescing = frontend.InvokeAsync("manual-quiesce", Guid.NewGuid(), "remote:work", false, payload, () => { }, default,
                    clientId: clientId).AsTask();
                await WaitFileAsync(Path.Combine(installed.ActivationDirectory, "quiesce-entered"));
                await Assert.ThrowsAsync<InvalidOperationException>(() => frontend.InvokeAsync("manual-run", Guid.NewGuid(), "remote:work", false,
                    payload, () => { }, default, clientId: clientId).AsTask());
                Assert.True(File.Exists(Path.Combine(installed.ActivationDirectory, "manual-stopped")));
                File.WriteAllText(Path.Combine(installed.ActivationDirectory, "quiesce-release"), "release");
                _ = await quiescing;
            }
            await WaitResultAsync(installed);
            state = ReadState(installed);
            Assert.Equal(hold ? "unknown" : "returned", state.Result!.Completion);
            Assert.Equal(hold ? (BusinessOutcome?)null : BusinessOutcome.NotApplicable, state.Result.Returned?.Business);
            var factsReply = await frontend.InvokeAsync("manual-state", null, "remote:work", false, payload, () => { }, default);
            using var facts = JsonDocument.Parse(factsReply.Content);
            var taskFact = Assert.Single(facts.RootElement.GetProperty("Tasks").EnumerateArray());
            Assert.Equal(taskId, taskFact.GetProperty("TaskId").GetGuid()); Assert.False(taskFact.GetProperty("IsRunning").GetBoolean());
            Assert.Equal(JsonValueKind.Null, taskFact.GetProperty("NextRunAt").ValueKind);
            var runFact = Assert.Single(taskFact.GetProperty("Runs").EnumerateArray());
            Assert.Equal(state.Source.Document.Invocation.RunId, runFact.GetProperty("RunId").GetGuid());
            Assert.Equal(state.Result.Completion, runFact.GetProperty("Completion").GetString());
            File.Delete(Path.Combine(installed.ActivationDirectory, "manual-task.json"));
            var replay = await frontend.InvokeAsync("manual-run", operation, "remote:work", false, payload, () => { }, default, clientId: Guid.NewGuid());
            using var replayJson = JsonDocument.Parse(replay.Content);
            Assert.False(replayJson.RootElement.GetProperty("IsNew").GetBoolean());
            Assert.Equal(state.Source.Document.Invocation.RunId, replayJson.RootElement.GetProperty("RunId").GetGuid());
        }
        finally
        {
            await schedules.StopAsync();
            await installed.Activator.StopAllAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact(DisplayName = "A Forgotten SDK Quiescence Lease Is Retired By The Original Host Callback Lifetime")]
    public async Task RetiresLeakedHold()
    {
        using var execution = new ExecutionAdmissionFixture(); using var catalog = execution.CreateCatalog();
        var source = execution.Source;
        var schedules = new PersistentScheduleService(source.Instance, source.Database, source.Lease, execution.Resources,
            execution.Lifecycle, execution.Index, execution.Service);
        using var installed = new InstallRuntimeFixture(managedMode: "frontend", catalog: catalog,
            portDependencies: new(schedules, null, null, null, null, null),
            managedManifestEdit: manifest => manifest["capabilities"]!.AsArray().Add("schedule.register"));
        var taskId = Guid.NewGuid();
        File.WriteAllBytes(Path.Combine(installed.ActivationDirectory, "manual-task.json"), JsonSerializer.SerializeToUtf8Bytes(new { TaskId = taskId, Revision = 7, Hold = false }));
        var frontend = await FrontendInstalledTests.OpenAsync(installed);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { taskId, revision = 7 }); var clientId = Guid.NewGuid();
        try
        {
            _ = await frontend.InvokeAsync("manual-leak", Guid.NewGuid(), "remote:work", false, payload, () => { }, default, clientId: clientId);
            _ = await frontend.InvokeAsync("manual-run", Guid.NewGuid(), "remote:work", false, payload, () => { }, default, clientId: clientId);
            await WaitResultAsync(installed);
            Assert.Equal("returned", ReadState(installed).Result!.Completion);
        }
        finally { await schedules.StopAsync(); await installed.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact(DisplayName = "The Current Exclusive SDK Callback Confirms An Exact Prior Task Writer And Rejects Unknown Run Identities")]
    public async Task ConfirmsPriorWriter()
    {
        using var execution = new ExecutionAdmissionFixture(); using var catalog = execution.CreateCatalog();
        var source = execution.Source;
        var schedules = new PersistentScheduleService(source.Instance, source.Database, source.Lease, execution.Resources,
            execution.Lifecycle, execution.Index, execution.Service);
        using var installed = new InstallRuntimeFixture(managedMode: "frontend", catalog: catalog,
            portDependencies: new(schedules, null, null, null, null, null),
            managedManifestEdit: manifest => manifest["capabilities"]!.AsArray().Add("schedule.register"));
        var taskId = Guid.NewGuid(); var input = Path.Combine(installed.ActivationDirectory, "manual-task.json");
        File.WriteAllBytes(input, JsonSerializer.SerializeToUtf8Bytes(new { TaskId = taskId, Revision = 7, Hold = false }));
        var frontend = await FrontendInstalledTests.OpenAsync(installed);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { taskId, revision = 7 }); var clientId = Guid.NewGuid();
        try
        {
            _ = await frontend.InvokeAsync("manual-run", Guid.NewGuid(), "remote:work", false, payload, () => { }, default, clientId: clientId);
            await WaitResultAsync(installed);
            var first = ReadState(installed); var previous = first.Source.Document.Invocation;
            File.WriteAllBytes(input, JsonSerializer.SerializeToUtf8Bytes(new { TaskId = taskId, Revision = 7, Hold = false,
                PreviousRunId = previous.RunId, PreviousAttemptId = previous.AttemptId }));
            _ = await frontend.InvokeAsync("manual-run", Guid.NewGuid(), "remote:work", false, payload, () => { }, default, clientId: clientId);
            await WaitFileAsync(Path.Combine(installed.ActivationDirectory, "writer-proof.json"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var states = new List<ManualWorkState>();
            while (true)
            {
                states.Clear(); ManualWorkInventory.Visit(catalog.Instance, catalog.Database, new(), states.Add);
                if (states.Count == 2 && states.All(state => state.Result is not null)) break;
                await Task.Delay(20, timeout.Token);
            }
            await schedules.StopAsync();
            using var proof = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(installed.ActivationDirectory, "writer-proof.json")));
            Assert.True(proof.RootElement.GetProperty("stopped").GetBoolean()); Assert.False(proof.RootElement.GetProperty("unknown").GetBoolean());
            Assert.Equal(2, states.Count); Assert.All(states, state => Assert.Equal("returned", state.Result!.Completion));
        }
        finally { await schedules.StopAsync(); await installed.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }
    }

    private static ManualWorkState ReadState(InstallRuntimeFixture installed)
    {
        var states = new List<ManualWorkState>();
        ManualWorkInventory.Visit(installed.Package.Catalog.Instance, installed.Package.Catalog.Database, new(), states.Add);
        return Assert.Single(states);
    }
    private static async Task WaitResultAsync(InstallRuntimeFixture installed)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (ReadState(installed).Result is null) await Task.Delay(20, timeout.Token);
    }
    private static async Task WaitFileAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(path)) await Task.Delay(20, timeout.Token);
    }
}
