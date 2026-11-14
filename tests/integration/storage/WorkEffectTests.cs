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
public sealed class WorkEffectTests
{
    [Fact(DisplayName = "Actual Atomic Token Rotation Advances The Same Callback Leases Only After Pure Identity Validation And Verified Commit")]
    public async Task ContinuesOwnedRotation()
    {
        using var execution = new ExecutionAdmissionFixture(); using var catalog = execution.CreateCatalog();
        var source = execution.Source; var taskId = Guid.NewGuid();
        var schedules = new PersistentScheduleService(source.Instance, source.Database, source.Lease, execution.Resources,
            execution.Lifecycle, execution.Index, execution.Service);
        var secrets = new SecretFieldService(source.Instance, source.Database, source.Lease, execution.Resources, execution.Index, new());
        using var installed = new InstallRuntimeFixture(managedMode: "frontend", catalog: catalog,
            portDependencies: new(schedules, secrets, new Policy(), null, null, null), managedManifestEdit: manifest =>
            {
                manifest["capabilities"]!.AsArray().Add("schedule.register");
                manifest["permissions"]!["secretAccess"] = new JsonArray("own-extension");
            });
        var documents = new SecretDocumentService(catalog.Instance, catalog.Database, catalog.Lease, catalog.Resources, catalog.Index, new());
        var fields = new[] { "credential", "one", "two", "three", "four", "five" };
        using (var material = new SecretValue("synthetic-shared-credential"))
            _ = await documents.WriteAsync(Guid.NewGuid(), "fixture:seed", "fixture-plugin", "tasks", 0, "{}"u8.ToArray(),
                fields.Select(field => new SecretDocumentEdit(taskId, field, 0, SecretDisposition.Set, material)).ToArray(), () => { }, default);
        File.WriteAllBytes(Path.Combine(installed.ActivationDirectory, "manual-task.json"),
            JsonSerializer.SerializeToUtf8Bytes(new { TaskId = taskId, Revision = 7, Hold = false, Effects = "rotate" }));
        var frontend = await FrontendInstalledTests.OpenAsync(installed); var operation = Guid.NewGuid();
        try
        {
            _ = await frontend.InvokeAsync("manual-run", operation, "fixture:editor", false,
                JsonSerializer.SerializeToUtf8Bytes(new { taskId, revision = 7 }), () => { }, default, clientId: Guid.NewGuid());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            ManualWorkState? state = null;
            while (state?.Result is null)
            {
                var states = new List<ManualWorkState>(); ManualWorkInventory.Visit(catalog.Instance, catalog.Database, new(), states.Add);
                state = states.Single(value => value.Source.Document.OperationId == operation);
                if (state.Result is null) await Task.Delay(20, timeout.Token);
            }
            Assert.Equal("returned", state.Result.Completion);
            using var report = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(installed.ActivationDirectory, "effects-" + taskId.ToString("D") + ".json")));
            Assert.Equal("Confirmed", report.RootElement.GetProperty("after").GetString());
            Assert.True(report.RootElement.GetProperty("rotationDenied").GetBoolean());
            Assert.True(report.RootElement.GetProperty("confirmedSubmitDenied").GetBoolean());
            foreach (var field in fields)
                Assert.Equal(field == "credential" ? 2 : 1, (await secrets.GetStateAsync(new("fixture-plugin", taskId, field), default)).Revision);
            WorkEffectInventory.Verify(catalog.Instance, catalog.Database, new());
            new SecretDocumentInventory(catalog.Instance, catalog.Database, new()).Verify();
            using var connection = DatabaseConnections.Open(catalog.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.All);
            using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM main.work_effect_rotations;";
            Assert.Equal(2L, command.ExecuteScalar());
            command.CommandText = "SELECT revision FROM extensions.extension_documents WHERE extension_id='fixture-plugin' AND name='tasks';";
            Assert.Equal(2L, command.ExecuteScalar());
        }
        finally { await schedules.StopAsync(); await installed.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }
    }

    [Theory(DisplayName = "Actual Installed Work Deduplicates Shared Credentials Across Tasks And Never Resubmits Busy Or Unknown Role Effects")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProtectsSharedEffects(bool concurrent)
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
        var first = Guid.NewGuid(); var second = Guid.NewGuid(); var input = Path.Combine(installed.ActivationDirectory, "manual-task.json");
        var documents = new SecretDocumentService(catalog.Instance, catalog.Database, catalog.Lease, catalog.Resources, catalog.Index, new());
        using (var material = new SecretValue("synthetic-shared-credential"))
            _ = await documents.WriteAsync(Guid.NewGuid(), "fixture:seed", "fixture-plugin", "tasks", 0, "{}"u8.ToArray(),
                [new(first, "credential", 0, SecretDisposition.Set, material), new(second, "credential", 0, SecretDisposition.Set, material)], () => { }, default);
        Write(first, concurrent ? "hold" : "submit");
        var frontend = await FrontendInstalledTests.OpenAsync(installed);
        try
        {
            var firstOperation = await Run(first);
            using var firstReport = await Report(first);
            Assert.Equal("New", firstReport.RootElement.GetProperty("game").GetString());
            Assert.Equal("New", firstReport.RootElement.GetProperty("role").GetString());
            Assert.True(firstReport.RootElement.GetProperty("forgedDenied").GetBoolean());
            Assert.True(firstReport.RootElement.GetProperty("completeDenied").GetBoolean());
            Assert.True(firstReport.RootElement.GetProperty("repeatDenied").GetBoolean());
            Assert.True(File.Exists(Path.Combine(installed.ActivationDirectory, "projection-ports-denied")));
            if (concurrent)
            {
                Write(second, "busy"); var busy = await Run(second); using var report = await Report(second);
                Assert.Equal("Busy", report.RootElement.GetProperty("game").GetString());
                await Completed(busy); File.Delete(Path.Combine(installed.ActivationDirectory, "effects-" + second.ToString("D") + ".json"));
                File.WriteAllText(Path.Combine(installed.ActivationDirectory, "effects-release"), "release");
            }
            await Completed(firstOperation);
            Write(second, "epoch-unknown"); var unknown = await Run(second); using var unrecorded = await Report(second);
            Assert.Equal("Unknown", unrecorded.RootElement.GetProperty("role").GetString());
            Assert.True(unrecorded.RootElement.GetProperty("submitDenied").GetBoolean());
            await Completed(unknown);
            using (var read = DatabaseConnections.Open(catalog.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.All))
            using (var count = read.CreateCommand())
            { count.CommandText = "SELECT COUNT(*) FROM main.work_effect_claims;"; Assert.Equal(2L, count.ExecuteScalar()); }
            File.Delete(Path.Combine(installed.ActivationDirectory, "effects-" + second.ToString("D") + ".json"));
            Write(second, "recheck"); var resolving = await Run(second); using var resolved = await Report(second);
            Assert.Equal("Unknown", resolved.RootElement.GetProperty("game").GetString());
            Assert.Equal("Unknown", resolved.RootElement.GetProperty("role").GetString());
            Assert.True(resolved.RootElement.GetProperty("submitDenied").GetBoolean());
            Assert.Equal("Confirmed", resolved.RootElement.GetProperty("after").GetString());
            Assert.True(resolved.RootElement.GetProperty("confirmedSubmitDenied").GetBoolean());
            Assert.True(resolved.RootElement.GetProperty("completedAcquireDenied").GetBoolean());
            await Completed(resolving);
            File.Delete(Path.Combine(installed.ActivationDirectory, "effects-" + first.ToString("D") + ".json"));
            Write(first, "confirm"); var confirmed = await Run(first); using var final = await Report(first);
            Assert.Equal("New", final.RootElement.GetProperty("game").GetString());
            Assert.True(final.RootElement.GetProperty("confirmedSubmitDenied").GetBoolean());
            Assert.True(final.RootElement.GetProperty("completedAcquireDenied").GetBoolean());
            await Completed(confirmed);
            WorkEffectInventory.Verify(catalog.Instance, catalog.Database, new());
            using var connection = DatabaseConnections.Open(catalog.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.All);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM main.work_effect_claims;"; Assert.Equal(4L, command.ExecuteScalar());
            command.CommandText = "SELECT COUNT(*) FROM main.work_effect_events WHERE kind='submitted';"; Assert.Equal(2L, command.ExecuteScalar());
            command.CommandText = "SELECT COUNT(*) FROM main.work_effect_events WHERE kind='confirmed';"; Assert.Equal(4L, command.ExecuteScalar());
            if (!concurrent)
            {
                connection.Close();
                using (var previous = DatabaseConnections.Open(catalog.Database.DatabasePath, domains: DatabaseDomain.All))
                {
                    using var convert = previous.CreateCommand();
                    convert.CommandText = "DROP TABLE main.work_effect_epoch_results; DROP TABLE main.work_effect_epochs; UPDATE main.schema_info SET contract_digest=$digest;";
                    convert.Parameters.AddWithValue("$digest", LegacyStateSchema.NotificationDigest); convert.ExecuteNonQuery();
                    Assert.Throws<IOException>(() => DatabaseSchema.VerifyCatalog(previous));
                }
                var before = File.ReadAllBytes(catalog.Database.DatabasePath);
                Assert.Throws<IOException>(() => StateDatabase.OpenExisting(catalog.Instance,
                    Path.Combine(catalog.Instance.RuntimeRoot, "legacy-effect-inspection"),
                    new NexusPipeline.Platform.Storage.Journals.JournalStartupAdmission(catalog.Instance, new()), catalog.Lease));
                Assert.Equal(before, File.ReadAllBytes(catalog.Database.DatabasePath));
            }
        }
        finally { await schedules.StopAsync(); await installed.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }

        void Write(Guid task, string mode) => File.WriteAllBytes(input,
            JsonSerializer.SerializeToUtf8Bytes(new { TaskId = task, Revision = 7, Hold = false, Effects = mode }));
        async Task<Guid> Run(Guid task)
        {
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
