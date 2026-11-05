using System.Text.Json;
using NexusPipeline.Core.Automation;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Extensions;
using Xunit;

namespace NexusPipeline.StorageTests;

internal static class ScriptProjectChecks
{
    internal static async Task VerifyAsync(ProjectSourceFixture project, InstallRuntimeFixture provider)
    {
        var catalog = project.Catalog;
        var definitions = new AutomationDefinitionStore(catalog.Database);
        var script = definitions.Read().Scripts.Single();
        var service = new ScriptProjectService(catalog.Instance, catalog.Database, catalog.Lease, catalog.Resources,
            catalog.Contracts, provider.Roots, provider.Activator);
        var context = service.ReadContext(script.Id, () => { }, "fixture-plugin");
        Assert.Equal(0, context.SelectionRevision); Assert.Null(context.Selection);
        Assert.Throws<UnauthorizedAccessException>(() => service.ReadContext(script.Id, () => { }, "foreign-provider"));
        var choice = JsonSerializer.SerializeToElement(new { mount = true, resourcePath = "assets" });
        var discovered = await service.DiscoverAsync(script.Id, script.Revision, "maas", choice, () => { }, default);
        Assert.True(discovered.GetProperty("Exists").GetBoolean());
        var operation = Guid.NewGuid();
        var saved = await service.SaveAsync(operation, "fixture", script.Id, script.Revision, 0, "maas", choice, () => { }, default);
        Assert.True(saved.IsNew); Assert.Equal(1, saved.Revision);
        var repeated = await service.SaveAsync(operation, "fixture", script.Id, script.Revision, 0, "maas", choice, () => { }, default);
        Assert.False(repeated.IsNew);
        context = service.ReadContext(script.Id, () => { }, "fixture-plugin");
        Assert.Equal(1, context.SelectionRevision); Assert.True(JsonElement.DeepEquals(choice, context.Selection!.Value));
        await using var previews = new ProjectPreviewService(catalog.Instance, catalog.Database, catalog.Lease, catalog.Resources,
            catalog.Contracts, provider.Roots, provider.Activator);
        await ScriptControlChecks.VerifyAsync(project, provider, service, previews);
        using var owner = new CancellationTokenSource();
        var preview = await previews.PrepareScriptAsync(service, "script-ui", script.Id, script.Revision, "maas", choice,
            () => { }, default, owner.Token);
        Assert.False(preview.CanExecute); Assert.Single(preview.Assets);
        Assert.Equal("{}", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(previews.Read("script-ui", preview.PreviewId, "index", () => { }).ContentBase64)));
        Assert.Throws<UnauthorizedAccessException>(() => previews.Read("account-ui", preview.PreviewId, "index", () => { }));
        Assert.Throws<UnauthorizedAccessException>(() => previews.Read("script-ui", preview.PreviewId, "artifact", () => { }));
        owner.Cancel();
        Assert.Throws<IOException>(() => previews.Read("script-ui", preview.PreviewId, "index", () => { }));
        var current = await previews.PrepareScriptAsync(service, "script-ui", script.Id, script.Revision, "maas", choice, () => { }, default);
        var before = definitions.Read();
        var change = new AutomationChanges(before.Revision, [], [script with { Name = "Changed script" }], [], [], [], [], []);
        definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", change), change, DateTimeOffset.UtcNow);
        Assert.Throws<IOException>(() => previews.Read("script-ui", current.PreviewId, "index", () => { }));
    }
}
