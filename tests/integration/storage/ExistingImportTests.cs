using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Control;
using NexusPipeline.Host.Control.Automation;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Platform.Storage.Automation;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ExistingImportTests
{
    [Fact(DisplayName = "Remote Definition Import Preserves Existing Identities And Completion Authority With Original Operation Replay")]
    public void ImportsExistingDefinitions()
    {
        using var fixture = new ExecutionAdmissionFixture();
        var catalog = SetLocalAction(fixture);
        var imported = catalog with { Queues = [catalog.Queues.Single() with { Name = "Imported metadata" }] };
        var operation = Guid.NewGuid(); var useCases = UseCases(fixture); var caller = Caller();
        var request = Command(operation, catalog, imported, "preview", new string('0', 64));
        var preview = JsonSerializer.SerializeToElement(useCases.Import(caller, "preview", request), JsonSerializerOptions.Web);
        Assert.True(preview.GetProperty("canApply").GetBoolean());
        var command = ApplyCommand(request, preview.GetProperty("previewDigest").GetString()!);
        _ = useCases.Import(caller, "apply", command);
        var saved = fixture.Source.Definitions.Read();
        Assert.Equal("Imported metadata", saved.Queues.Single().Name);
        Assert.Equal(AutomationCompletionAction.Shutdown, saved.Queues.Single().CompletionAction);
        Assert.Equal(catalog.Scripts.Select(value => value.Id), saved.Scripts.Select(value => value.Id));
        Assert.Equal(catalog.Bindings.Select(value => value.Id), saved.Bindings.Select(value => value.Id));
        var replay = JsonSerializer.SerializeToElement(useCases.Import(caller, "apply", command), JsonSerializerOptions.Web);
        Assert.False(replay.GetProperty("isNew").GetBoolean());
        Assert.Equal(saved.Revision, fixture.Source.Definitions.Read().Revision);
    }

    [Fact(DisplayName = "A Concurrent Catalog Change Invalidates An Existing Definition Import Preview Without Overwriting Metadata")]
    public void RejectsStalePreview()
    {
        using var fixture = new ExecutionAdmissionFixture(); var catalog = SetLocalAction(fixture);
        var operation = Guid.NewGuid(); var useCases = UseCases(fixture); var caller = Caller();
        var request = Command(operation, catalog, catalog, "preview", new string('0', 64));
        var preview = JsonSerializer.SerializeToElement(useCases.Import(caller, "preview", request), JsonSerializerOptions.Web);
        var changes = new AutomationChanges(catalog.Revision, [], [], [], [], [catalog.Queues.Single() with { Name = "Concurrent edit" }], [], []);
        fixture.Source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "concurrent-editor", changes), changes, DateTimeOffset.UtcNow);
        Assert.Throws<AutomationConflictException>(() => useCases.Import(caller, "apply",
            ApplyCommand(request, preview.GetProperty("previewDigest").GetString()!)));
        Assert.Equal("Concurrent edit", fixture.Source.Definitions.Read().Queues.Single().Name);
    }

    [Theory(DisplayName = "Existing Definition Revision Binding Cannot Grant Remote New Or Changed Completion Authority")]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsActionChanges(bool newIdentity)
    {
        using var fixture = new ExecutionAdmissionFixture(); var catalog = SetLocalAction(fixture);
        var queue = catalog.Queues.Single() with { CompletionAction = AutomationCompletionAction.Restart };
        if (newIdentity) queue = queue with { Id = Guid.NewGuid() };
        var input = catalog with { Queues = [queue] };
        Assert.Throws<UnauthorizedAccessException>(() => UseCases(fixture).Import(Caller(), "preview",
            Command(Guid.NewGuid(), catalog, input, "preview", new string('0', 64))));
        Assert.Equal(catalog.Revision, fixture.Source.Definitions.Read().Revision);
    }

    private static AutomationCatalog SetLocalAction(ExecutionAdmissionFixture fixture)
    {
        var catalog = fixture.Source.Definitions.Read();
        var changes = new AutomationChanges(catalog.Revision, [], [], [], [],
            [catalog.Queues.Single() with { CompletionAction = AutomationCompletionAction.Shutdown }], [], []);
        fixture.Source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "local-declaration", changes), changes, DateTimeOffset.UtcNow);
        return fixture.Source.Definitions.Read();
    }

    private static ExchangeUseCases UseCases(ExecutionAdmissionFixture fixture)
    {
        var source = fixture.Source;
        return new(new(source.Instance, new(), source.Database, source.Lease, fixture.Resources, fixture.Index),
            new(source.Instance, new(), source.Database, source.Lease, fixture.Resources, fixture.Index), new(new()));
    }

    private static AuthenticatedControlRequest Caller() => new(ControlOrigin.Remote, "existing-import", null, () => new(false, false));

    private static JsonElement ApplyCommand(JsonElement preview, string digest)
    {
        var command = JsonNode.Parse(preview.GetRawText())!;
        command["action"] = "apply"; command["previewDigest"] = digest;
        return JsonSerializer.SerializeToElement(command);
    }

    private static JsonElement Command(Guid operation, AutomationCatalog catalog, AutomationCatalog imported, string action, string digest) =>
        JsonSerializer.SerializeToElement(new
        {
            format = "nexus.control.automation-import", version = 1, operationId = operation, action,
            expectedRevision = catalog.Revision, previewDigest = digest,
            bundle = JsonSerializer.Deserialize<JsonElement>(new AutomationBundleCodec(new()).Encode(imported, DateTimeOffset.Parse("2026-11-01T00:00:00Z"))),
            roots = imported.Roots.Select(value => new { value.Id, value.Path }), attachments = Array.Empty<object>()
        }, JsonSerializerOptions.Web);
}
