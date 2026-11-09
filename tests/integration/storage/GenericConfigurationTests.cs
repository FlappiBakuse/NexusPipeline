using System.Collections.Immutable;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class GenericConfigurationTests
{
    [Theory(DisplayName = "Missing Or Corrupt Saved Content Cannot Become Verified Execution Inputs Or Be Replaced By Live Bytes")]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsMissingContent(bool corrupt)
    {
        using var fixture = new PlanBuilderFixture();
        var catalog = fixture.Definitions.Read();
        var script = catalog.Scripts.Single(script => script.Id == fixture.ScriptId);
        var binding = catalog.Bindings.Single(binding => binding.Id == fixture.BindingA);
        var saved = GenericConfigurationPolicy.RequireSaved(binding, script, script.ConfigurationPaths[0],
            new(fixture.Instance, fixture.Database, new()));
        var entry = Assert.Single(saved.Tree.Entries, entry => entry.Kind == FileObjectKind.File);
        var content = Path.Combine(fixture.Instance.RuntimeRoot, "blobs", "content", entry.Sha256 + ".bin");
        var original = File.ReadAllBytes(content);
        if (corrupt) { var changed = original.ToArray(); changed[0] ^= 255; File.WriteAllBytes(content, changed); }
        else File.Delete(content);
        try
        {
            var before = fixture.Snapshot();
            using var draft = fixture.Prepare(new BindingTarget(binding.Id));
            Assert.Null(draft.ReadPlan());
            var item = Assert.Single(draft.Explanation.Items);
            Assert.Equal(ExecutionExplanationDisposition.Blocked, item.Disposition);
            Assert.Equal("execution_input_unavailable", item.ReasonCode);
            AssertUnchanged(fixture, before);
        }
        finally { File.WriteAllBytes(content, original); }
    }

    [Theory(DisplayName = "Configuration Readiness Uses The Actual Run Group Resource Budget Before Declaring An Executable Plan")]
    [InlineData(32, true)]
    [InlineData(33, false)]
    [InlineData(64, false)]
    public void EnforcesResourceBudget(int count, bool allowed)
    {
        using var fixture = new PlanBuilderFixture();
        var catalog = fixture.Definitions.Read();
        var original = catalog.Scripts.Single(script => script.Id == fixture.ScriptId);
        var paths = Enumerable.Range(0, count).Select(index => new AutomationPath(original.ConfigurationPaths[0].RootId,
            "extra-" + index + ".json")).ToImmutableArray();
        var changes = new AutomationChanges(catalog.Revision, [], [original with { ConfigurationPaths = paths }], [], [], [], [], []);
        fixture.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", changes), changes, DateTimeOffset.UtcNow);
        catalog = fixture.Definitions.Read();
        var binding = catalog.Bindings.Single(binding => binding.Id == fixture.BindingA);
        var root = catalog.Roots.Single(root => root.Id == paths[0].RootId);
        foreach (var path in paths)
        {
            var target = Path.Combine(root.Path, path.RelativePath); File.WriteAllText(target, "{\"fixture\":true}");
            var tree = new FileTreeArchive(new(SnapshotBudget.Default), new(fixture.Instance, new())).Capture(target, default).Snapshot;
            Save(fixture, binding, path, 0, tree);
        }
        var before = fixture.Snapshot();
        using var draft = fixture.Prepare(new BindingTarget(binding.Id));
        Assert.Equal(allowed, draft.Explanation.HasRunnable); Assert.Equal(!allowed, draft.Explanation.HasBlockedItems);
        if (allowed) Assert.Equal(count, Assert.IsType<FrozenExecutionPlan>(draft.ReadPlan()).ReadWire().ConfigurationSlots.Count);
        else Assert.Equal("execution_configuration_resource_limit", Assert.Single(draft.Explanation.Items).ReasonCode);
        AssertUnchanged(fixture, before);
    }

    [Fact(DisplayName = "A New Saved Revision After Dry Run Cannot Be Persisted As The Old Drafts Frozen Input")]
    public void RejectsAdvancedSnapshots()
    {
        using var fixture = new PlanBuilderFixture();
        using var draft = fixture.Prepare(new BindingTarget(fixture.BindingA));
        var catalog = fixture.Definitions.Read();
        var script = catalog.Scripts.Single(script => script.Id == fixture.ScriptId);
        var binding = catalog.Bindings.Single(binding => binding.Id == fixture.BindingA);
        var saved = GenericConfigurationPolicy.RequireSaved(binding, script, script.ConfigurationPaths[0], new(fixture.Instance, fixture.Database, new()));
        Save(fixture, binding, script.ConfigurationPaths[0], saved.Revision, saved.Tree);
        var before = fixture.Snapshot();
        Assert.Equal("configuration_revision_conflict", Assert.Throws<ConfigurationException>(() => fixture.Builder.Freeze(draft)).Message);
        AssertUnchanged(fixture, before);
    }

    [Fact(DisplayName = "Generic Configuration Identity Uses Only The Canonical Portable Root And Relative Declaration")]
    public void UsesCanonicalDeclarations()
    {
        var root = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var path = new AutomationPath(root, "configs/设置.json");
        var resource = GenericConfigurationPolicy.ResourceId(path);
        Assert.Equal(resource, GenericConfigurationPolicy.ResourceId(new(root, path.RelativePath)));
        Assert.NotEqual(resource, GenericConfigurationPolicy.ResourceId(path with { RootId = Guid.NewGuid() }));
        Assert.NotEqual(resource, GenericConfigurationPolicy.ResourceId(path with { RelativePath = "other/设置.json" }));
    }

    [Theory(DisplayName = "Invalid Portable Declarations Cannot Identify A Generic Configuration Resource")]
    [InlineData("empty-root")]
    [InlineData("empty-path")]
    [InlineData("parent")]
    [InlineData("absolute")]
    [InlineData("backslash")]
    [InlineData("null")]
    public void RejectsInvalidDeclarations(string fault)
    {
        var path = fault switch
        {
            "null" => null,
            "empty-root" => new AutomationPath(Guid.Empty, "settings.json"),
            _ => new AutomationPath(Guid.NewGuid(), fault switch
            { "empty-path" => "", "parent" => "../settings.json", "absolute" => "D:/settings.json", _ => "configs\\settings.json" })
        };
        Assert.Equal("configuration_generic_path_invalid", Assert.Throws<ConfigurationException>(() => GenericConfigurationPolicy.ResourceId(path!)).Message);
    }

    [Fact(DisplayName = "Preview Is Read Only And Admission Selects The Saved Generic Configuration Bytes")]
    public void ReadsSavedConfiguration()
    {
        using var fixture = new ExecutionAdmissionFixture();
        var source = fixture.Source;
        var original = File.ReadAllBytes(source.Configuration);
        const string live = "{\"externalChanged\":true}";
        File.WriteAllText(source.Configuration, live);
        var before = source.Snapshot();
        var request = source.Request(new BindingTarget(source.BindingA));
        using (var draft = fixture.Service.DryRun(request, PlanBuilderFixture.CreatedAt))
            Assert.Equal(1, Assert.Single(Assert.IsType<FrozenExecutionPlan>(draft.ReadPlan()).ReadWire().ConfigurationSlots).ExpectedRevision);
        AssertUnchanged(source, before);

        var operation = Guid.NewGuid();
        fixture.Service.Start(operation, "saved-configuration", request, PlanBuilderFixture.CreatedAt);
        using var accepted = fixture.Take(operation);
        var candidate = Assert.Single(accepted.Prepared.Configurations.Values)
            .Use(items => Assert.Single(Assert.Single(items).Plan.Candidate.Contents));
        using var saved = new ImmutableBlobStore(source.Instance, new()).OpenVerified(candidate);
        using var contents = new MemoryStream();
        saved.CopyTo(contents);
        Assert.Equal(original, contents.ToArray());
        Assert.Equal(live, File.ReadAllText(source.Configuration));
    }

    [Theory(DisplayName = "Missing Saved Configuration Blocks A Single Binding And Skips Its Queue Items Without Adopting Live Files")]
    [InlineData(false)]
    [InlineData(true)]
    public void IsolatesMissingInputs(bool queue)
    {
        using var fixture = new PlanBuilderFixture(saveConfigurations: false);
        var before = fixture.Snapshot();
        using var draft = fixture.Prepare(queue ? new QueueTarget(fixture.QueueId) : new BindingTarget(fixture.BindingA));
        Assert.Equal(!queue, draft.Explanation.HasBlockedItems); Assert.Equal(queue, draft.Explanation.HasRunnable);
        if (queue) Assert.NotNull(draft.ReadPlan()); else Assert.Null(draft.ReadPlan());
        Assert.All(draft.Explanation.Items.Where(item => item.Source.Script.Id == fixture.ScriptId),
            item => { Assert.Equal(queue ? ExecutionExplanationDisposition.Skipped : ExecutionExplanationDisposition.Blocked,
                item.Disposition); Assert.Equal("configuration_required", item.ReasonCode); });
        if (queue) Assert.Equal(ExecutionExplanationDisposition.Runnable,
            Assert.Single(draft.Explanation.Items, item => item.Source.Binding.Id == fixture.OtherBinding).Disposition);
        AssertUnchanged(fixture, before);
    }

    [Theory(DisplayName = "A Generic Saved Snapshot Cannot Borrow Another Contract Account Or Undeclared Provider Path")]
    [InlineData("contract", "configuration_required")]
    [InlineData("owner", "configuration_owner_changed")]
    [InlineData("declaration", "configuration_generic_definition_unbound")]
    [InlineData("specialized", "configuration_generic_definition_unbound")]
    public void RejectsForeignSnapshots(string fault, string reason)
    {
        using var fixture = new PlanBuilderFixture();
        var catalog = fixture.Definitions.Read();
        var script = catalog.Scripts.Single(script => script.Id == fixture.ScriptId);
        var binding = catalog.Bindings.Single(binding => binding.Id == fixture.BindingA);
        var path = script.ConfigurationPaths[0];
        var store = new ConfigurationRevisionStore(fixture.Instance, fixture.Database, new());
        if (fault == "contract")
        {
            var saved = GenericConfigurationPolicy.RequireSaved(binding, script, path, store);
            var change = new ConfigurationRevisionChange(saved.Address, saved.Revision, binding.Revision,
                saved.Contract with { DefinitionSha256 = new('f', 64) }, saved.Tree, ConfigurationUpdateKind.Manual);
            store.Commit(ConfigurationRevisionStore.Identity(Guid.NewGuid(), "fixture", change), change, DateTimeOffset.UtcNow);
        }
        if (fault == "owner") binding = binding with { UserId = fixture.UserB };
        if (fault == "declaration") path = path with { RelativePath = "foreign.json" };
        if (fault == "specialized") script = script with { Kind = AutomationKind.Specialized };
        var before = fixture.Snapshot();
        Assert.Equal(reason, Assert.Throws<ConfigurationException>(() => GenericConfigurationPolicy.RequireSaved(binding, script, path, store)).Message);
        AssertUnchanged(fixture, before);
    }

    private static void AssertUnchanged(PlanBuilderFixture fixture, (string[] Rows, string[] Files) before)
    {
        var after = fixture.Snapshot(); Assert.Equal(before.Rows, after.Rows); Assert.Equal(before.Files, after.Files);
    }

    private static void Save(PlanBuilderFixture fixture, AutomationBinding binding, AutomationPath path, long expected, FileTreeSnapshot tree)
    {
        var change = new ConfigurationRevisionChange(new(binding.Id, GenericConfigurationPolicy.ResourceId(path)), expected, binding.Revision,
            GenericConfigurationPolicy.Contract(path), tree, ConfigurationUpdateKind.Manual);
        new ConfigurationRevisionStore(fixture.Instance, fixture.Database, new()).Commit(
            ConfigurationRevisionStore.Identity(Guid.NewGuid(), "fixture", change), change, DateTimeOffset.UtcNow);
    }
}
