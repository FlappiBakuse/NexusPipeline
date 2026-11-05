using System.Text.Json;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Extensions;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class ScriptChoiceTests
{
    [Fact(DisplayName = "Script Compilation Reads The Declared Physical Configuration Without Borrowing Any Saved Account Mount")]
    public async Task ReadsScriptConfiguration()
    {
        using var fixture = new ProjectSourceFixture(configuration: true, file: true);
        var owner = fixture.Catalog;
        var script = new AutomationDefinitionStore(owner.Database).Read().Scripts.Single();
        var saved = fixture.Revisions.ReadStored(fixture.Address)!;
        var bytes = "{\"account\":\"local-script\"}"u8.ToArray();
        File.WriteAllBytes(fixture.ConfigurationFile!, bytes);
        using var source = SelectedProjectSource.ForScript(owner.Instance, owner.Database, owner.Lease, owner.Resources,
            script.Id, script.Revision, "maas", () => { });
        using var port = new ScriptSourcePort(source, () => { }, default);
        var configuration = Assert.Single(port.Configurations);
        Assert.Equal("local.json", configuration.Path); Assert.Equal(script.Revision, configuration.Revision);
        Assert.Equal(bytes, (await port.ReadAsync(configuration.Path, default))!.Content.ToArray());
        Assert.Throws<IOException>(() => File.WriteAllText(fixture.ConfigurationFile!, "changed"));
        Assert.Equal(saved.Manifest, fixture.Revisions.ReadStored(fixture.Address)!.Manifest);
        source.RequireCurrent();
    }

    [Fact(DisplayName = "Script Choices Save With Independent Cas And Idempotency Without Changing Account Configuration Or Source Files")]
    public void SavesIndependentChoice()
    {
        using var fixture = new ProjectSourceFixture(configuration: true, file: true);
        var owner = fixture.Catalog;
        var script = new AutomationDefinitionStore(owner.Database).Read().Scripts.Single();
        var original = File.ReadAllBytes(fixture.ConfigurationFile!);
        var account = fixture.Revisions.ReadStored(fixture.Address)!;
        using var source = SelectedProjectSource.ForScript(owner.Instance, owner.Database, owner.Lease, owner.Resources,
            script.Id, script.Revision, "maas", () => { });
        var store = new ScriptChoiceStore(owner.Instance, owner.Database, owner.Lease, owner.Resources);
        var selection = JsonSerializer.SerializeToElement(new { InterfacePath = "assets/interface.json", Language = "zh_cn" });
        Assert.Null(store.Read(source, "maa-framework"));
        var operation = Guid.NewGuid();
        var saved = store.Save(operation, "fixture", 0, source, "maa-framework", selection, () => { });
        Assert.True(saved.IsNew); Assert.Equal(1, saved.Revision);
        var repeated = store.Save(operation, "fixture", 0, source, "maa-framework", selection, () => { });
        Assert.False(repeated.IsNew); Assert.Equal(saved.Revision, repeated.Revision);
        var reopened = store.Read(source, "maa-framework")!;
        Assert.Equal(script.Revision, reopened.ScriptRevision);
        Assert.True(JsonElement.DeepEquals(selection, reopened.Selection));
        Assert.Throws<InvalidOperationException>(() => store.Save(Guid.NewGuid(), "fixture", 0, source, "maa-framework", selection, () => { }));
        Assert.Throws<OperationConflictException>(() => store.Save(operation, "other", 0, source, "maa-framework", selection, () => { }));
        using var binding = fixture.Open();
        Assert.Equal("project_script_authority_required", Assert.Throws<IOException>(() => store.Read(binding, "maa-framework")).Message);
        Assert.Equal(account.Manifest, fixture.Revisions.ReadStored(fixture.Address)!.Manifest);
        Assert.Equal(account.Revision.Revision, fixture.Revisions.ReadStored(fixture.Address)!.Revision.Revision);
        Assert.Equal(original, File.ReadAllBytes(fixture.ConfigurationFile!));
    }
}
