using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Control;
using NexusPipeline.Host.Control.Automation;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Platform.Storage.Automation;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class CompletionDeclarationTests
{
    [Theory(DisplayName = "Real Automation Changes Require Local Authority Only To Introduce Or Change A Non None Completion Action")]
    [InlineData(ControlOrigin.Remote, "create", false)]
    [InlineData(ControlOrigin.Remote, "change", false)]
    [InlineData(ControlOrigin.Remote, "metadata", true)]
    [InlineData(ControlOrigin.Remote, "revoke", true)]
    [InlineData(ControlOrigin.Mcp, "create", false)]
    [InlineData(ControlOrigin.Mcp, "change", false)]
    [InlineData(ControlOrigin.Mcp, "metadata", false)]
    [InlineData(ControlOrigin.Mcp, "revoke", false)]
    [InlineData(ControlOrigin.Local, "create", true)]
    [InlineData(ControlOrigin.Local, "change", true)]
    public void EnforcesChangeAuthority(ControlOrigin origin, string behavior, bool allowed)
    {
        using var fixture = new ExecutionAdmissionFixture(); var source = fixture.Source;
        var catalog = source.Definitions.Read();
        var existing = catalog.Queues.Single() with { CompletionAction = AutomationCompletionAction.Shutdown };
        var initial = new AutomationChanges(catalog.Revision, [], [], [], [], [existing], [], []);
        source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "completion-local", initial), initial, DateTimeOffset.UtcNow);
        catalog = source.Definitions.Read(); existing = catalog.Queues.Single();
        var value = existing with { Name = "Edited queue", CompletionAction = behavior == "revoke" ? AutomationCompletionAction.None :
            behavior is "create" or "change" ? AutomationCompletionAction.Restart : existing.CompletionAction };
        if (behavior == "create") value = value with { Id = Guid.NewGuid(), Revision = 0 };
        var changes = new AutomationChanges(catalog.Revision, [], [], [], [], [value], [], []);
        var service = new AutomationDefinitionService(source.Instance, new(), source.Database, source.Lease, fixture.Resources, fixture.Index);
        var useCases = new AutomationUseCases(service, new(new()));
        var caller = new AuthenticatedControlRequest(origin, "completion-caller", null, () => new(true, true));
        var before = fixture.Count("operations");
        if (allowed)
        {
            _ = useCases.Change(caller, new(Guid.NewGuid(), changes));
            Assert.Equal(catalog.Revision + 1, source.Definitions.Read().Revision);
            Assert.Equal(value.CompletionAction, source.Definitions.Read().Queues.Single(queue => queue.Id == value.Id).CompletionAction);
        }
        else
        {
            Assert.Throws<UnauthorizedAccessException>(() => useCases.Change(caller, new(Guid.NewGuid(), changes)));
            Assert.Equal(catalog.Revision, source.Definitions.Read().Revision);
            Assert.Equal(before, fixture.Count("operations"));
        }
    }

    [Theory(DisplayName = "Exchange Preview Uses The Same Local Completion Authority Before Any Import Is Accepted")]
    [InlineData(ControlOrigin.Remote, false)]
    [InlineData(ControlOrigin.Mcp, false)]
    [InlineData(ControlOrigin.Local, true)]
    public void EnforcesImportAuthority(ControlOrigin origin, bool allowed)
    {
        using var fixture = new ExecutionAdmissionFixture(); var source = fixture.Source;
        var catalog = source.Definitions.Read();
        var bundle = catalog with { Roots = [], Scripts = [], Users = [], Bindings = [], Schedules = [],
            Queues = [catalog.Queues.Single() with { Id = Guid.NewGuid(), ScriptIds = [], CompletionAction = AutomationCompletionAction.Hibernate }] };
        var codec = new AutomationBundleCodec(new());
        var service = new AutomationDefinitionService(source.Instance, new(), source.Database, source.Lease, fixture.Resources, fixture.Index);
        var imports = new AutomationImportService(source.Instance, new(), source.Database, source.Lease, fixture.Resources, fixture.Index);
        var useCases = new ExchangeUseCases(service, imports, codec);
        var command = JsonSerializer.SerializeToElement(new { operationId = Guid.NewGuid(), expectedRevision = catalog.Revision,
            bundle = JsonSerializer.Deserialize<JsonElement>(codec.Encode(bundle, DateTimeOffset.UtcNow)),
            roots = Array.Empty<object>(), attachments = Array.Empty<object>() }, JsonSerializerOptions.Web);
        var caller = new AuthenticatedControlRequest(origin, "completion-import", null, () => new(true, true));
        var before = fixture.Count("operations");
        if (allowed) Assert.NotNull(useCases.Import(caller, "preview", command));
        else Assert.Throws<UnauthorizedAccessException>(() => useCases.Import(caller, "preview", command));
        Assert.Equal(before, fixture.Count("operations"));
        Assert.Equal(catalog.Revision, source.Definitions.Read().Revision);
    }
}
