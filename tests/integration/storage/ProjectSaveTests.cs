using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;
using System.Text.Json.Nodes;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ProjectSaveTests
{
    [Fact(DisplayName = "A Publication Guard Rechecks Caller Authority Even Immediately After A Successful Compilation Guard")]
    public async Task RechecksPublicationCaller()
    {
        await using var fixture = new ProjectSaveFixture();
        await fixture.StartAsync();
        var revoked = false;
        var result = await fixture.Service.UseAsync(fixture.Project.Binding, 1, "maas", JsonSerializer.SerializeToElement(new { }),
            () => { if (revoked) throw new UnauthorizedAccessException(); }, (_, current, _) =>
            {
                current();
                revoked = true;
                Assert.Throws<UnauthorizedAccessException>(current);
                return ValueTask.FromResult(true);
            }, default);
        Assert.True(result);
    }

    [Fact(DisplayName = "Protected Project Save Rechecks Authority After Preparing The Choice And Before Publishing")]
    public async Task RejectsRevokedPublication()
    {
        await using var fixture = new ProjectSaveFixture();
        await fixture.StartAsync();
        var project = fixture.Project;
        var operation = Guid.NewGuid();
        var binding = project.Binding;
        var selectionBytes = """{"protected":true,"publicationProbe":"after-compilation"}"""u8.ToArray();
        var choice = JsonSerializer.Deserialize<JsonElement>(selectionBytes);
        using var secret = new SecretValue("PROTECTED_REVOCATION_FIXTURE");
        ProjectProtectedEdit[] edits = [new(new("input-fixture", 0, SecretDisposition.Set), secret)];
        var digest = Convert.ToHexStringLower(SHA256.HashData(selectionBytes));
        var prepared = Path.Combine(project.Catalog.Instance.RuntimeRoot, "blobs", "content", digest + ".bin");
        var address = ProjectChoiceStore.Address(binding);
        var fields = new SecretFieldStore(project.Catalog.Instance, project.Catalog.Database);
        var field = new SecretFieldKey("fixture-plugin", binding, "input-fixture");

        Assert.False(File.Exists(prepared));
        var checkedBeforePublication = false;
        var refused = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.SaveAsync(operation, "editor",
            binding, 1, 0, "maas", choice, () =>
            {
                // SaveProtectedAsync persists the selection after compilation and before admitting the document write.
                if (!File.Exists(prepared)) return;
                checkedBeforePublication = true;
                throw new UnauthorizedAccessException("fixture_caller_revoked");
            }, DateTimeOffset.UtcNow, default, edits, "fixture-plugin").AsTask());
        Assert.Equal("fixture_caller_revoked", refused.Message);
        Assert.True(checkedBeforePublication);
        Assert.Equal(selectionBytes, File.ReadAllBytes(prepared));
        Assert.Null(project.Revisions.Read(address));
        Assert.Equal(0, (await fields.GetStateAsync(field, default)).Revision);
        Assert.Null(fixture.Secrets.ReadCommit(operation, "fixture-plugin", "editor"));
        using (var connection = DatabaseConnections.Open(project.Catalog.Database.DatabasePath, readOnly: true))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT count(*) FROM main.operations WHERE operation_id=$id;";
            command.Parameters.AddWithValue("$id", operation.ToString("D"));
            Assert.Equal(0L, (long)command.ExecuteScalar()!);
        }

        var saved = await fixture.Service.SaveAsync(operation, "editor", binding, 1, 0, "maas", choice,
            () => { }, DateTimeOffset.UtcNow, default, edits, "fixture-plugin");
        Assert.Equal(operation, saved.OperationId);
        Assert.Equal(1, project.Revisions.Read(address)!.Revision);
        Assert.Equal(1, (await fields.GetStateAsync(field, default)).Revision);
        Assert.NotNull(fixture.Secrets.ReadCommit(operation, "fixture-plugin", "editor"));
    }

    [Theory(DisplayName = "Protected Save Settlement Drains Only Its Callback And Proves Absence Without Overwriting Accepted Or Committed Owners")]
    [InlineData("before-admission")]
    [InlineData("preparing")]
    [InlineData("accepted")]
    [InlineData("committed")]
    public async Task SettlesOriginalSave(string stage)
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        await using var fixture = new ProjectSaveFixture();
        await fixture.StartAsync(stage == "accepted" ? new Observe(point =>
        {
            if (point != SecretPreparationPoint.Accepted) return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new IOException("fixture_release_timeout");
        }) : null);
        var project = fixture.Project; var catalog = project.Catalog; var service = fixture.Service;
        var operation = Guid.NewGuid(); var choice = JsonSerializer.SerializeToElement(new { @protected = true, wait = stage == "preparing" });
        using var value = new SecretValue("PROTECTED_SETTLEMENT_PRIVATE_SENTINEL");
        ProjectProtectedEdit[] edits = [new(new("input-fixture", 0, SecretDisposition.Set), value)];
        var intents = edits.Select(edit => edit.Intent).ToArray();
        Task<Core.Configuration.ConfigurationSaveResult>? saving = null;
        if (stage != "before-admission")
        {
            saving = Task.Run(async () => await service.SaveAsync(operation, "editor", project.Binding, 1, 0, "maas", choice,
                () => { }, DateTimeOffset.UtcNow, default, edits, "fixture-plugin"));
            if (stage == "preparing") await fixture.WaitAsync("compiler-entered");
            if (stage == "accepted") Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            if (stage == "committed") _ = await saving;
        }
        var settlingService = fixture.Reopen();
        var settled = settlingService.FinishAsync(operation, "editor", project.Binding, 1, 0, "maas", "fixture-plugin", choice, intents,
            () => { }, default).AsTask();
        if (stage == "accepted") { Assert.False(settled.IsCompleted); release.Set(); }
        var outcome = await settled;
        Assert.Equal(stage == "accepted" ? "pending" : stage == "committed" ? "committed" : "notAccepted", outcome.Outcome);
        if (saving is not null && stage != "committed") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => saving);
        var reopened = fixture.Reopen();
        var observed = reopened.ReadOutcome(operation, "editor", project.Binding, 1, 0, "maas", "fixture-plugin", choice, intents, () => { });
        Assert.Equal(outcome.Outcome, observed.Outcome);
        if (outcome.Outcome == "notAccepted")
        {
            var late = await Assert.ThrowsAsync<IOException>(() => reopened.SaveAsync(operation, "editor", project.Binding, 1, 0, "maas", choice,
                () => { }, DateTimeOffset.UtcNow, default, edits, "fixture-plugin").AsTask());
            Assert.Equal("project_operation_closed", late.Message);
            Assert.Null(project.Revisions.Read(ProjectChoiceStore.Address(project.Binding)));
            Assert.Throws<OperationConflictException>(() => reopened.ReadOutcome(operation, "another-caller", project.Binding, 1, 0,
                "maas", "fixture-plugin", choice, intents, () => { }));
            Assert.Throws<OperationConflictException>(() => reopened.ReadOutcome(operation, "editor", project.Binding, 1, 0,
                "maas", "another-provider", choice, intents, () => { }));
        }
        else
        {
            using var connection = DatabaseConnections.Open(catalog.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.All);
            using var command = connection.CreateCommand(); command.CommandText = "SELECT operation_kind,status FROM main.operations WHERE operation_id=$id;";
            command.Parameters.AddWithValue("$id", operation.ToString("D")); using var row = command.ExecuteReader();
            Assert.True(row.Read()); Assert.Equal("extension.document.write", row.GetString(0));
            Assert.Equal(stage == "accepted" ? "accepted" : "completed", row.GetString(1));
            Assert.Equal(stage == "committed" ? 1 : 0, (await new SecretFieldStore(catalog.Instance, catalog.Database)
                .GetStateAsync(new("fixture-plugin", project.Binding, "input-fixture"), default)).Revision);
        }
    }

    [Fact(DisplayName = "Closing A Project Operation Fences Late Admissions While Waiting For Exactly Its Active Callback")]
    public async Task FencesLateAdmission()
    {
        var flights = new ProjectSaveFlights(); var operation = Guid.NewGuid();
        using var other = flights.Begin(Guid.NewGuid(), "editor", "other");
        var callback = flights.Begin(operation, "editor", "original");
        using var closing = flights.Close(operation, "editor", "original");
        Assert.True(callback.Cancellation.IsCancellationRequested); Assert.False(other.Cancellation.IsCancellationRequested);
        Assert.Throws<IOException>(() => flights.Begin(operation, "editor", "original"));
        Assert.Throws<OperationConflictException>(() => flights.Close(operation, "another-caller", "original"));
        var drained = closing.DrainAsync(default); Assert.False(drained.IsCompleted);
        callback.Dispose(); await drained;
        Assert.Throws<IOException>(() => flights.Begin(operation, "editor", "original"));
    }

    private sealed class Observe(Action<SecretPreparationPoint> observe) : ISecretPreparationObserver
    { public void Reached(SecretPreparationPoint point, Guid journalId) => observe(point); }
}

[SupportedOSPlatform("windows")]
internal sealed class ProjectSaveFixture : IAsyncDisposable
{
    internal ProjectSourceFixture Project { get; }
    internal ProjectSaveFixture(bool configuration = false) { Project = new(configuration: configuration); }
    internal InstallRuntimeFixture Provider { get; private set; } = null!;
    private InstallRuntimeFixture? consumer;
    internal ProjectCompilationService Service { get; private set; } = null!;
    internal SecretDocumentService Secrets { get; private set; } = null!;

    internal async Task StartAsync(ISecretPreparationObserver? observer = null)
    {
        Project.Write("interface.json", "{\"task\":[]}"u8.ToArray());
        Provider = new(managedMode: "frontend", catalog: Project.Catalog, managedManifestEdit: manifest =>
        {
            manifest["capabilities"]!.AsArray().Add("source.compile"); manifest["managed"]!["compilers"] = new JsonArray("project");
            manifest["permissions"]!["fileAccess"]!.AsArray().Add("selected-script-root");
            manifest["permissions"]!["secretAccess"] = new JsonArray("selected-binding");
        });
        File.WriteAllText(Path.Combine(Provider.ActivationDirectory, "compiler-mode"), "enabled");
        _ = await FrontendInstalledTests.OpenAsync(Provider);
        var manifest = JsonNode.Parse(Provider.Package.ManifestBytes)!.AsObject();
        manifest["extensionId"] = "maas"; manifest["artifactName"] = "FixtureConsumer"; manifest["kind"] = "data-specialized";
        manifest.Remove("managed"); manifest.Remove("frontend"); manifest.Remove("execution");
        manifest["capabilities"] = new JsonArray("task.adapt", "configuration.edit");
        manifest["permissions"] = new JsonObject { ["fileAccess"] = new JsonArray("selected-script-root") };
        manifest["specialized"] = new JsonObject { ["adapterEntry"] = "adapter/main.js", ["compiler"] = new JsonObject
            { ["extensionId"] = "fixture-plugin", ["compilerId"] = "project" } };
        consumer = new(catalog: Project.Catalog, originalManifest: manifest,
            managedPayloadEdit: directory =>
            {
                Directory.CreateDirectory(Path.Combine(directory, "adapter"));
                File.WriteAllText(Path.Combine(directory, "adapter", "main.js"), "globalThis.nexusTaskAdapter = () => ({});");
            },
            payloadRoots: Provider.Roots, existingActivator: Provider.Activator, catalogSequence: 2);
        _ = await consumer.Session().ApplyAsync(consumer.Prepared, default);
        consumer.Prepared.Dispose(); consumer.Verified.Dispose(); consumer.Root.Dispose();
        var catalog = Project.Catalog;
        Secrets = new(catalog.Instance, catalog.Database, catalog.Lease, catalog.Resources, catalog.Index, catalog.Contracts, observer);
        Service = Reopen();
    }

    internal ProjectCompilationService Reopen() => new(Project.Catalog.Instance, Project.Catalog.Database, Project.Catalog.Lease,
        Project.Catalog.Resources, Project.Catalog.Contracts, Provider.Roots, Provider.Activator, Secrets);

    internal async Task WaitAsync(string name)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(Path.Combine(Provider.ActivationDirectory, name))) await Task.Delay(10, timeout.Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (Provider is not null) await Provider.Activator.StopAllAsync(TimeSpan.FromSeconds(10));
        consumer?.Dispose(); Provider?.Dispose(); Project.Dispose();
    }
}
