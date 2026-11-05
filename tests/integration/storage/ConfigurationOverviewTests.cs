using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Configuration;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class ConfigurationOverviewTests
{
    [Fact(DisplayName = "A Generic Configuration Overview Binds Its Unsaved And Captured Snapshot To The Declared Resource")]
    public void BindsGenericRevisions()
    {
        using var fixture = new PlanBuilderFixture(saveConfigurations: false);
        var catalog = fixture.Definitions.Read();
        var binding = catalog.Bindings.Single(item => item.Id == fixture.BindingA);
        var script = catalog.Scripts.Single(item => item.Id == fixture.ScriptId);
        var path = Assert.Single(script.ConfigurationPaths);
        var overview = new ConfigurationOverviewReader(fixture.Instance, fixture.Database, fixture.Lease, new ContractReader());

        var beforeSave = overview.Read(binding.Id);
        var unsaved = Assert.Single(beforeSave.Resources);
        var resourceId = GenericConfigurationPolicy.ResourceId(path);
        Assert.Equal(binding.Id, beforeSave.BindingId);
        Assert.Equal(binding.Revision, beforeSave.BindingRevision);
        Assert.Equal(script.Id, beforeSave.ScriptId);
        Assert.Equal(script.Revision, beforeSave.ScriptRevision);
        Assert.Equal(resourceId, unsaved.ResourceId);
        Assert.Equal(0, unsaved.Revision);
        Assert.False(unsaved.IsSaved);
        Assert.Equal(FileObjectKind.Missing, unsaved.Shape);

        var root = catalog.Roots.Single(item => item.Id == path.RootId);
        var target = Path.Combine(root.Path, path.RelativePath);
        var captured = new FileTreeArchive(new(SnapshotBudget.Default), new(fixture.Instance, new()))
            .Capture(target, CancellationToken.None).Snapshot;
        var change = new ConfigurationRevisionChange(new(binding.Id, resourceId), 0, binding.Revision,
            GenericConfigurationPolicy.Contract(path), captured, ConfigurationUpdateKind.Manual);
        var revisions = new ConfigurationRevisionStore(fixture.Instance, fixture.Database, new ContractReader());
        revisions.Commit(ConfigurationRevisionStore.Identity(Guid.NewGuid(), "configuration-overview", change), change, DateTimeOffset.UtcNow);

        var afterSave = overview.Read(binding.Id);
        var saved = Assert.Single(afterSave.Resources);
        Assert.Equal(binding.Id, afterSave.BindingId);
        Assert.Equal(binding.Revision, afterSave.BindingRevision);
        Assert.Equal(script.Id, afterSave.ScriptId);
        Assert.Equal(script.Revision, afterSave.ScriptRevision);
        Assert.Equal(resourceId, saved.ResourceId);
        Assert.Equal(1, saved.Revision);
        Assert.True(saved.IsSaved);
        Assert.Equal(FileObjectKind.File, saved.Shape);
        Assert.Equal(1, saved.FileCount);
    }

    [Fact(DisplayName = "A Specialized Configuration Overview Binds Its Resource And Rejects A Foreign Owner Contract")]
    public void BindsSpecializedContract()
    {
        using var fixture = new PlanBuilderFixture(specialized: true, saveConfigurations: false);
        var definitions = fixture.Definitions;
        var catalog = definitions.Read();
        var binding = catalog.Bindings.Single(item => item.Id == fixture.BindingA);
        var script = catalog.Scripts.Single(item => item.Id == fixture.ScriptId);
        var declaration = new AutomationAdapterResource("native-settings", 0, "", 1,
            ConfigurationSyntax.Json, new([], [], []));
        var declare = new AutomationChanges(catalog.Revision, [], [script with { AdapterResources = [declaration] }],
            [], [], [], [], []);
        definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "configuration-overview", declare), declare, DateTimeOffset.UtcNow);
        catalog = definitions.Read();
        script = catalog.Scripts.Single(item => item.Id == fixture.ScriptId);
        binding = catalog.Bindings.Single(item => item.Id == fixture.BindingA);
        declaration = Assert.Single(script.AdapterResources);
        var path = script.ConfigurationPaths[declaration.DeclarationIndex];
        var root = catalog.Roots.Single(item => item.Id == path.RootId);
        var target = Path.Combine(root.Path, path.RelativePath);
        var captured = new FileTreeArchive(new(SnapshotBudget.Default), new(fixture.Instance, new()))
            .Capture(target, CancellationToken.None).Snapshot;
        var change = new ConfigurationRevisionChange(new(binding.Id, declaration.ResourceId), 0, binding.Revision,
            AdapterConfigurationPolicy.Contract(script, declaration), captured, ConfigurationUpdateKind.Manual);
        var revisions = new ConfigurationRevisionStore(fixture.Instance, fixture.Database, new ContractReader());
        revisions.Commit(ConfigurationRevisionStore.Identity(Guid.NewGuid(), "configuration-overview", change), change, DateTimeOffset.UtcNow);

        var overview = new ConfigurationOverviewReader(fixture.Instance, fixture.Database, fixture.Lease, new ContractReader());
        var current = overview.Read(binding.Id);
        var resource = Assert.Single(current.Resources);
        Assert.Equal(AutomationKind.Specialized, current.Kind);
        Assert.Equal(script.ExtensionId, current.ExtensionId);
        Assert.Equal(declaration.ResourceId, resource.ResourceId);
        Assert.Equal(declaration.Syntax, resource.Syntax);
        Assert.Equal(1, resource.Revision);
        Assert.True(resource.IsSaved);

        var wrongOwner = script with { ExtensionId = "foreign-extension" };
        var wrongContract = change with { ExpectedRevision = 1,
            Contract = AdapterConfigurationPolicy.Contract(wrongOwner, declaration) };
        revisions.Commit(ConfigurationRevisionStore.Identity(Guid.NewGuid(), "configuration-overview", wrongContract),
            wrongContract, DateTimeOffset.UtcNow);
        Assert.Equal("configuration_required",
            Assert.Throws<ConfigurationException>(() => overview.Read(binding.Id)).Message);
    }
}
