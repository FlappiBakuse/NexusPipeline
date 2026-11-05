using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class WorkEpochTests
{
    [Theory(DisplayName = "Actual Installed Callbacks Reopen A Completed Game After Its Closer Stops And Keep Per Role Confirmation")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoordinatesDistinctRoles(bool concurrent)
    {
        using var execution = new ExecutionAdmissionFixture(); using var catalog = execution.CreateCatalog();
        var source = execution.Source;
        var schedules = new PersistentScheduleService(source.Instance, source.Database, source.Lease, execution.Resources,
            execution.Lifecycle, execution.Index, execution.Service);
        var secrets = new SecretFieldService(source.Instance, source.Database, source.Lease, execution.Resources, execution.Index, new());
        using var installed = new InstallRuntimeFixture(managedMode: "frontend", catalog: catalog,
            portDependencies: new(schedules, secrets, new Policy(), null, null, null), managedManifestEdit: manifest =>
            {
                manifest["capabilities"]!.AsArray().Add("schedule.register");
                manifest["permissions"]!["secretAccess"] = new JsonArray("own-extension");
            });
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var documents = new SecretDocumentService(catalog.Instance, catalog.Database, catalog.Lease, catalog.Resources, catalog.Index, new());
        using (var material = new SecretValue("synthetic-shared-credential"))
            _ = await documents.WriteAsync(Guid.NewGuid(), "fixture:seed", "fixture-plugin", "tasks", 0, "{}"u8.ToArray(),
                [new(first, "credential", 0, SecretDisposition.Set, material), new(second, "credential", 0, SecretDisposition.Set, material)], () => { }, default);
        File.WriteAllBytes(Path.Combine(installed.ActivationDirectory, "manual-task.json"),
            JsonSerializer.SerializeToUtf8Bytes(new { TaskId = first, Revision = 7, Hold = false, Effects = "epoch-a" }));
        var frontend = await FrontendInstalledTests.OpenAsync(installed);
        try
        {
            var initial = await Run(first, concurrent ? "epoch-a-hold" : "epoch-a");
            using var a = await Report(first);
            Assert.Equal("New", a.RootElement.GetProperty("role").GetString());
            Assert.Equal("Confirmed", a.RootElement.GetProperty("after").GetString());
            if (concurrent)
            {
                var overlap = await Run(second, "epoch-b"); using var busy = await Report(second);
                Assert.Equal("Busy", busy.RootElement.GetProperty("game").GetString());
                Assert.Equal(JsonValueKind.Null, busy.RootElement.GetProperty("role").ValueKind);
                await Completed(overlap); File.WriteAllText(Path.Combine(installed.ActivationDirectory, "effects-release"), "release");
            }
            await Completed(initial);
            var next = await Run(second, "epoch-b"); using var b = await Report(second);
            Assert.Equal("New", b.RootElement.GetProperty("game").GetString());
            Assert.Equal("New", b.RootElement.GetProperty("role").GetString());
            await Completed(next);
            var repeat = await Run(first, "epoch-a"); using var saved = await Report(first);
            Assert.Equal("New", saved.RootElement.GetProperty("game").GetString());
            Assert.Equal("Confirmed", saved.RootElement.GetProperty("role").GetString());
            Assert.True(saved.RootElement.GetProperty("submitDenied").GetBoolean());
            Assert.Equal("Confirmed", saved.RootElement.GetProperty("after").GetString());
            await Completed(repeat);
            WorkEffectInventory.Verify(catalog.Instance, catalog.Database, new());
            using var connection = DatabaseConnections.Open(catalog.Database.DatabasePath, readOnly: true);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM work_effect_claims;"; Assert.Equal(3L, command.ExecuteScalar());
            command.CommandText = "SELECT COUNT(*) FROM work_effect_events WHERE kind='submitted';"; Assert.Equal(2L, command.ExecuteScalar());
            command.CommandText = "SELECT COUNT(*) FROM work_effect_epochs;"; Assert.Equal(3L, command.ExecuteScalar());
            command.CommandText = "SELECT COUNT(*) FROM work_effect_epoch_results;"; Assert.Equal(3L, command.ExecuteScalar());
            command.CommandText = "SELECT COUNT(*) FROM work_effect_events WHERE kind='confirmed';"; Assert.Equal(3L, command.ExecuteScalar());
        }
        finally { await schedules.StopAsync(); await installed.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }

        async Task<Guid> Run(Guid task, string mode)
        {
            var report = Path.Combine(installed.ActivationDirectory, "effects-" + task.ToString("D") + ".json");
            if (File.Exists(report)) File.Delete(report);
            File.WriteAllBytes(Path.Combine(installed.ActivationDirectory, "manual-task.json"),
                JsonSerializer.SerializeToUtf8Bytes(new { TaskId = task, Revision = 7, Hold = false, Effects = mode }));
            var operation = Guid.NewGuid();
            _ = await frontend.InvokeAsync("manual-run", operation, "fixture:editor", false,
                JsonSerializer.SerializeToUtf8Bytes(new { taskId = task, revision = 7 }), () => { }, default, clientId: Guid.NewGuid());
            return operation;
        }
        async Task<JsonDocument> Report(Guid task)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var path = Path.Combine(installed.ActivationDirectory, "effects-" + task.ToString("D") + ".json");
            while (!File.Exists(path)) await Task.Delay(20, timeout.Token);
            return JsonDocument.Parse(File.ReadAllBytes(path));
        }
        async Task Completed(Guid operation)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (true)
            {
                var states = new List<ManualWorkState>(); ManualWorkInventory.Visit(catalog.Instance, catalog.Database, new(), states.Add);
                var state = states.Single(value => value.Source.Document.OperationId == operation);
                if (state.Result is not null) { Assert.Equal("returned", state.Result.Completion); return; }
                await Task.Delay(20, timeout.Token);
            }
        }
    }

    private sealed class Policy : ISecretAccessPolicy
    {
        public void AssertAllowed(SecretFieldKey key, SecretAccessKind access, SecretSubjectScopes declaredScopes)
        { if (!declaredScopes.HasFlag(SecretSubjectScopes.OwnExtension)) throw new UnauthorizedAccessException(); }
    }
}
