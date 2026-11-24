using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class InstalledWorkTests
{
    [Fact(DisplayName = "An Installed Task Shows Its Newest Sixteen Runs And Its Actual Current Callback Across Other Task History")]
    public async Task ReadsRecentTaskHistory()
    {
        using var execution = new ExecutionAdmissionFixture();
        using var catalog = execution.CreateCatalog();
        var source = execution.Source;
        var schedules = new PersistentScheduleService(source.Instance, source.Database, source.Lease, execution.Resources,
            execution.Lifecycle, execution.Index, execution.Service);
        using var installed = new InstallRuntimeFixture(managedMode: "frontend", catalog: catalog,
            portDependencies: new(schedules, null, null, null, null, null),
            managedManifestEdit: manifest => manifest["capabilities"]!.AsArray().Add("schedule.register"));
        var task = Guid.NewGuid();
        var other = Guid.NewGuid();
        var client = Guid.NewGuid();
        var input = Path.Combine(installed.ActivationDirectory, "manual-task.json");
        var history = new List<Guid>();
        var invocations = new Dictionary<Guid, (Guid RunId, Guid AttemptId)>();
        Write(task, hold: false);
        var frontend = await FrontendInstalledTests.OpenAsync(installed);
        try
        {
            for (var index = 0; index < 23; index++)
            {
                var selected = index % 8 == 7 || index == 22 ? other : task;
                Write(selected, hold: false);
                var operation = await Start(selected);
                if (selected == task) history.Add(operation);
                await WaitFor(operation, "completed");
            }
            Assert.Equal(20, history.Count);
            Write(task, hold: false);
            await CheckHistory(false, history, "returned");

            File.Delete(Path.Combine(installed.ActivationDirectory, "manual-entered.json"));
            Write(task, hold: true, previous: invocations[history[0]]);
            var active = await Start(task);
            await WaitFileAsync(Path.Combine(installed.ActivationDirectory, "manual-entered.json"));
            await WaitFor(active, "accepted");
            using (var proof = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(installed.ActivationDirectory, "writer-proof.json"))))
            {
                Assert.True(proof.RootElement.GetProperty("stopped").GetBoolean());
                Assert.False(proof.RootElement.GetProperty("unknown").GetBoolean());
            }
            await CheckHistory(true, history.Append(active).ToArray(), "running");

            var newer = new List<Guid>();
            for (var index = 0; index < 16; index++)
            {
                var operation = await Start(task);
                await WaitFor(operation, "completed");
                newer.Add(operation);
            }
            await CheckHistory(true, newer, "unknown");
        }
        finally { await schedules.StopAsync(); await installed.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }

        void Write(Guid taskId, bool hold, (Guid RunId, Guid AttemptId)? previous = null) => File.WriteAllBytes(input,
            JsonSerializer.SerializeToUtf8Bytes(new { TaskId = taskId, Revision = 7, Hold = hold,
                PreviousRunId = previous?.RunId, PreviousAttemptId = previous?.AttemptId }));

        async Task<Guid> Start(Guid taskId)
        {
            var id = Guid.NewGuid();
            var reply = await frontend.InvokeAsync("manual-run", id, "remote:work", false,
                JsonSerializer.SerializeToUtf8Bytes(new { taskId, revision = 7 }), () => { }, default, clientId: client);
            using var receipt = JsonDocument.Parse(reply.Content);
            Assert.True(receipt.RootElement.GetProperty("IsNew").GetBoolean());
            invocations.Add(id, (receipt.RootElement.GetProperty("RunId").GetGuid(), receipt.RootElement.GetProperty("AttemptId").GetGuid()));
            return id;
        }

        async Task WaitFor(Guid id, string status)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (true)
            {
                using (var connection = DatabaseConnections.Open(source.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime))
                using (var check = DatabaseConnections.Command(connection, null, "SELECT status FROM main.operations WHERE operation_id=$0;", id.ToString("D")))
                    if ((string?)check.ExecuteScalar() == status) return;
                await Task.Delay(20, timeout.Token);
            }
        }

        async Task CheckHistory(bool running, IReadOnlyList<Guid> accepted, string newestCompletion)
        {
            var reply = await frontend.InvokeAsync("manual-state", null, "remote:work", false,
                JsonSerializer.SerializeToUtf8Bytes(new { taskId = task, revision = 7 }), () => { }, default, clientId: client);
            using var state = JsonDocument.Parse(reply.Content);
            var result = Assert.Single(state.RootElement.GetProperty("Tasks").EnumerateArray());
            Assert.Equal(running, result.GetProperty("IsRunning").GetBoolean());
            var runs = result.GetProperty("Runs").EnumerateArray().ToArray();
            Assert.Equal(accepted.Reverse().Take(16).Select(id => invocations[id].RunId), runs.Select(value => value.GetProperty("RunId").GetGuid()));
            Assert.Equal(newestCompletion, runs[0].GetProperty("Completion").GetString());
        }
    }

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
