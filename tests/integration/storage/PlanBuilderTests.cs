using System.Security.Cryptography;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Settings;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;
using ExecutionTarget = NexusPipeline.Core.Execution.ExecutionTarget;

namespace NexusPipeline.StorageTests;

public sealed class PlanBuilderTests
{
    [Fact(DisplayName = "Generic Preparation Uses Actual Imported Definitions Default Settings And Verified Native Inputs")]
    public void PreparesVerifiedInputs()
    {
        using var fixture = new PlanBuilderFixture();
        Assert.Equal(0, fixture.Settings.Read().Revision);
        Assert.Equal(HostSettings.Default.LogLevel, fixture.Settings.Read().Values.LogLevel);
        using var draft = fixture.Prepare(new BindingTarget(fixture.BindingA));
        var frozen = Assert.IsType<FrozenExecutionPlan>(draft.ReadPlan()); var plan = frozen.ReadWire();
        var step = Assert.Single(plan.Steps);
        Assert.True(draft.Explanation.HasRunnable); Assert.False(draft.Explanation.HasBlockedItems);
        Assert.Equal(0, plan.SettingsRevision); Assert.Equal(1, plan.Version);
        Assert.Equal(fixture.Instance.InstanceId.ToString("D"), plan.InstanceId);
        Assert.Equal(fixture.ScriptId.ToString("D"), step.ScriptId); Assert.Equal(fixture.UserA.ToString("D"), step.UserId);
        Assert.Equal(fixture.BindingA.ToString("D"), step.BindingId); Assert.Equal("process", step.EngineKind);
        Assert.Equal(fixture.Image, step.ExecutablePath); Assert.Equal(new FileInfo(fixture.Image).Length, step.ImageSizeBytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(fixture.Image))), step.ImageSha256);
        var identity = PhysicalObjectIdentity.Read(fixture.Image);
        Assert.Equal(identity.VolumeSerial, step.ImageIdentity.VolumeSerial); Assert.Equal(identity.FileId, step.ImageIdentity.FileId);
        Assert.Equal(Path.GetDirectoryName(fixture.Image), step.WorkingDirectory);
        Assert.Equal(new[] { "--fixture-input-only" }, step.Arguments);
        Assert.Equal(new[] { fixture.Configuration }, step.ConfigurationTargets); Assert.Equal(new[] { fixture.Log }, step.LogTargets.Select(log => log.ScopePath));
        Assert.Empty(step.Tasks); Assert.Empty(step.SelectedTaskIds); Assert.Empty(plan.ExtensionLocks);
        Assert.Equal("unknown", plan.RetryPolicy.Safety); Assert.False(plan.RetryPolicy.HostApproved);
        Assert.Equal(1, plan.RetryPolicy.MaximumAttempts); Assert.Equal(0, plan.RetryPolicy.BackoffSeconds);
        Assert.Equal(5, plan.Budget.StopTimeoutSeconds); Assert.Equal(6, plan.Budget.DrainTimeoutSeconds);
        Assert.Equal(7, plan.Budget.JudgementTimeoutSeconds); Assert.Equal(120, plan.Budget.TotalTimeoutSeconds);
        Assert.All(plan.SourceRevisions, item => Assert.Equal(1, item.Revision));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(frozen.Export())), draft.PlanDigest);
        Assert.False(File.Exists(Path.Combine(fixture.Instance.RuntimeRoot, plan.DefinitionBundle.RelativePath)));
        Assert.False(File.Exists(Path.Combine(fixture.Instance.RuntimeRoot, "blobs", "plans", draft.PlanDigest + ".json")));
    }

    [Fact(DisplayName = "Script Preparation Uses Global User Order Then Canonical Identity Instead Of Binding Priority")]
    public void PreservesUserOrder()
    {
        using var fixture = new PlanBuilderFixture();
        using var draft = fixture.Prepare(new ScriptTarget(fixture.ScriptId)); var plan = draft.ReadPlan()!.ReadWire();
        Assert.Equal(new[] { fixture.BindingB, fixture.BindingC, fixture.BindingA }.Select(Id), plan.Selection.Select(item => item.BindingId));
        Assert.Equal(new[] { fixture.UserB, fixture.UserC, fixture.UserA }.Select(Id), plan.Steps.Select(step => step.UserId));
        Assert.Empty(plan.Steps[0].DependsOn);
        Assert.Empty(plan.Steps[1].DependsOn);
        Assert.Empty(plan.Steps[2].DependsOn);
        Assert.Equal("none", plan.CompletionAction); Assert.Null(plan.QueueId);
    }

    [Fact]
    public void ExpandsActiveBindings()
    {
        using var fixture = new PlanBuilderFixture(); fixture.Disable(fixture.BindingB);
        using var draft = fixture.Prepare(new QueueTarget(fixture.QueueId)); var plan = draft.ReadPlan()!.ReadWire();
        Assert.Equal(new[] { fixture.BindingC, fixture.BindingA, fixture.OtherBinding }.Select(Id),
            plan.Selection.Select(item => item.BindingId));
        Assert.All(plan.Selection, item => Assert.Equal("runnable", item.Disposition));
        Assert.DoesNotContain(plan.Selection, item => item.BindingId == Id(fixture.BindingB));
        Assert.Equal(new[] { fixture.BindingC, fixture.BindingA, fixture.OtherBinding }.Select(Id), plan.Steps.Select(item => item.BindingId));
        Assert.Equal("exitHost", plan.CompletionAction); Assert.Equal(Id(fixture.QueueId), plan.QueueId);
        Assert.Equal(3, draft.Explanation.Items.Length); Assert.False(draft.Explanation.HasBlockedItems);
        Assert.Empty(plan.Steps[1].DependsOn);
        Assert.Empty(plan.Steps[2].DependsOn);
    }

    [Theory(DisplayName = "Direct Targets Do Not Expand Another Binding For The Same Account")]
    [InlineData("binding")]
    [InlineData("script-user")]
    public void SelectsExactBindings(string target)
    {
        using var fixture = new PlanBuilderFixture();
        ExecutionTarget requested = target == "binding" ? new BindingTarget(fixture.BindingB) : new ScriptUserTarget(fixture.ScriptId, fixture.UserB);
        using var draft = fixture.Prepare(requested); var plan = draft.ReadPlan()!.ReadWire();
        Assert.Equal(Id(fixture.BindingB), Assert.Single(plan.Selection).BindingId);
        Assert.Equal(Id(fixture.BindingB), Assert.Single(plan.Steps).BindingId);
        Assert.Equal(fixture.BindingB, Assert.Single(draft.Explanation.Items).Source.Binding.Id);
    }

    [Theory(DisplayName = "Missing Empty And Entirely Disabled Targets Return Reasons Without Inventing A Plan")]
    [InlineData("all-disabled", "execution_no_enabled_bindings")]
    [InlineData("empty-queue", "execution_queue_empty")]
    [InlineData("missing-binding", "execution_binding_missing")]
    [InlineData("missing-script", "execution_script_missing")]
    [InlineData("missing-user", "execution_user_missing")]
    [InlineData("missing-queue", "execution_queue_missing")]
    public void ExplainsUnavailableTargets(string scenario, string reason)
    {
        using var fixture = new PlanBuilderFixture();
        if (scenario == "all-disabled") fixture.Disable(fixture.BindingA, fixture.BindingB, fixture.BindingC);
        if (scenario == "empty-queue") fixture.EmptyQueue();
        ExecutionTarget target = scenario switch
        {
            "all-disabled" => new ScriptTarget(fixture.ScriptId), "empty-queue" => new QueueTarget(fixture.QueueId),
            "missing-binding" => new BindingTarget(Guid.NewGuid()), "missing-script" => new ScriptTarget(Guid.NewGuid()),
            "missing-user" => new ScriptUserTarget(fixture.ScriptId, Guid.NewGuid()), _ => new QueueTarget(Guid.NewGuid())
        };
        var before = fixture.Snapshot();
        using var draft = fixture.Prepare(target);
        Assert.Null(draft.ReadPlan()); Assert.Null(draft.PlanDigest); Assert.False(draft.Explanation.HasRunnable);
        Assert.Equal(reason, draft.Explanation.ReasonCode);
        if (scenario == "all-disabled")
        {
            Assert.Equal(3, draft.Explanation.Items.Length);
            Assert.All(draft.Explanation.Items, item => Assert.Equal(ExecutionExplanationDisposition.Skipped, item.Disposition));
        }
        else Assert.Empty(draft.Explanation.Items);
        AssertUnchanged(fixture, before);
    }

    [Theory(DisplayName = "Extension Preferences Cannot Substitute For An Actual Specialized Provider")]
    [InlineData(null, "execution_extension_provider_unavailable")]
    [InlineData(true, "execution_extension_provider_unavailable")]
    [InlineData(false, "execution_extension_disabled")]
    public void RequiresActualProviders(bool? enabled, string reason)
    {
        using var fixture = new PlanBuilderFixture(specialized: true); fixture.SetExtensionPreference(enabled);
        using var draft = fixture.Prepare(new QueueTarget(fixture.QueueId));
        var plan = Assert.IsType<FrozenExecutionPlan>(draft.ReadPlan()).ReadWire(); Assert.NotNull(draft.PlanDigest);
        Assert.Equal(Id(fixture.OtherBinding), Assert.Single(plan.Steps).BindingId);
        Assert.True(draft.Explanation.HasRunnable); Assert.False(draft.Explanation.HasBlockedItems);
        Assert.Equal(ExecutionExplanationDisposition.Runnable,
            Assert.Single(draft.Explanation.Items, item => item.Source.Binding.Id == fixture.OtherBinding).Disposition);
        foreach (var item in draft.Explanation.Items.Where(item => item.Source.Script.Id == fixture.ScriptId))
        {
            Assert.Equal(ExecutionExplanationDisposition.Skipped, item.Disposition); Assert.Equal(reason, item.ReasonCode);
        }
    }

    [Fact]
    public void SkipsUnavailableGroups()
    {
        using var fixture = new PlanBuilderFixture(); File.Delete(fixture.OtherImage);
        using var draft = fixture.Prepare(new QueueTarget(fixture.QueueId));
        Assert.NotNull(draft.ReadPlan()); Assert.NotNull(draft.PlanDigest); Assert.Equal(4, draft.Explanation.Items.Length);
        var blocked = Assert.Single(draft.Explanation.Items, item => item.Source.Binding.Id == fixture.OtherBinding);
        Assert.Equal(ExecutionExplanationDisposition.Skipped, blocked.Disposition); Assert.Equal("execution_input_unavailable", blocked.ReasonCode);
        Assert.All(draft.Explanation.Items.Where(item => item != blocked), item => Assert.Equal(ExecutionExplanationDisposition.Runnable, item.Disposition));
    }

    [Theory(DisplayName = "Preparation And Reading Its Draft Do Not Write Any Database Row Blob Plan Or Native Scene")]
    [InlineData("runnable")]
    [InlineData("blocked")]
    [InlineData("disabled")]
    public void PreviewsWithoutWrites(string scenario)
    {
        using var fixture = new PlanBuilderFixture();
        if (scenario == "blocked") File.Delete(fixture.OtherImage);
        if (scenario == "disabled") fixture.Disable(fixture.BindingA, fixture.BindingB, fixture.BindingC, fixture.OtherBinding);
        var before = fixture.Snapshot();
        using (var draft = fixture.Prepare(new QueueTarget(fixture.QueueId)))
        {
            _ = draft.ReadPlan()?.ReadWire(); _ = draft.ReadPlan()?.Export();
            Assert.Equal(scenario != "disabled", draft.ReadPlan() is not null);
            AssertUnchanged(fixture, before);
        }
        AssertUnchanged(fixture, before);
    }

    [Fact(DisplayName = "Caller Changes To Returned Wire Collections And Exported Bytes Do Not Alter The Opaque Draft")]
    public void OwnsDraftCollections()
    {
        using var fixture = new PlanBuilderFixture();
        using var draft = fixture.Prepare(new ScriptTarget(fixture.ScriptId));
        var before = draft.ReadPlan()!.Export(); var digest = draft.PlanDigest;
        var read = draft.ReadPlan()!; var wire = read.ReadWire();
        var steps = Assert.IsAssignableFrom<IList<ExecutionPlanStep>>(wire.Steps);
        steps[0] = steps[0] with { Arguments = ["changed returned copy"], SelectedTaskIds = ["invented"] };
        var selection = Assert.IsAssignableFrom<IList<ExecutionPlanSelectionItem>>(wire.Selection);
        selection[0] = selection[0] with { ReasonCode = "changed_copy" };
        var bytes = read.Export(); Array.Fill(bytes, (byte)0);
        Assert.Equal(before, read.Export()); Assert.Equal(before, draft.ReadPlan()!.Export()); Assert.Equal(digest, draft.PlanDigest);
        Assert.Equal(ExecutionSelectionPolicy.InputsVerified, draft.Explanation.Items[0].ReasonCode);
    }

    [Theory(DisplayName = "Real Image Changes After Dry Run Are Rejected Before Any Frozen Input Is Persisted")]
    [InlineData("replace")]
    [InlineData("delete")]
    [InlineData("empty")]
    public void RejectsChangedImages(string change)
    {
        using var fixture = new PlanBuilderFixture();
        using var draft = fixture.Prepare(new BindingTarget(fixture.BindingA)); var original = draft.ReadPlan()!.ReadWire();
        if (change == "replace")
        {
            File.Move(fixture.Image, fixture.Image + ".original");
            File.Copy(Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!, fixture.Image);
        }
        else if (change == "delete") File.Delete(fixture.Image);
        else File.WriteAllBytes(fixture.Image, []);
        var before = fixture.Snapshot();
        Assert.IsAssignableFrom<IOException>(Record.Exception(() => fixture.Builder.Freeze(draft)));
        AssertUnchanged(fixture, before);
        using var refreshed = fixture.Prepare(new BindingTarget(fixture.BindingA));
        if (change == "replace")
        {
            var step = Assert.Single(refreshed.ReadPlan()!.ReadWire().Steps);
            Assert.NotEqual(original.Steps[0].ImageIdentity.FileId, step.ImageIdentity.FileId);
            Assert.Equal(original.Steps[0].ImageSha256, step.ImageSha256);
        }
        else
        {
            Assert.Null(refreshed.ReadPlan()); Assert.Equal("execution_input_unavailable", Assert.Single(refreshed.Explanation.Items).ReasonCode);
        }
        AssertUnchanged(fixture, before);
    }

    [Fact(DisplayName = "Disposed Draft Rejects Further Reads Without Removing Its Independent Explanation")]
    public void RejectsDisposedDrafts()
    {
        using var fixture = new PlanBuilderFixture(); var before = fixture.Snapshot();
        var draft = fixture.Prepare(new BindingTarget(fixture.BindingA)); var explanation = draft.Explanation;
        draft.Dispose(); draft.Dispose();
        Assert.Throws<ObjectDisposedException>(() => draft.ReadPlan());
        var retained = draft.Explanation;
        Assert.True(retained.HasRunnable); Assert.Equal(explanation.ReasonCode, retained.ReasonCode);
        Assert.Equal(explanation.Items.ToArray(), retained.Items.ToArray());
        Assert.Equal(explanation.Groups.ToArray(), retained.Groups.ToArray());
        AssertUnchanged(fixture, before);
    }

    [Theory(DisplayName = "Preparation Requires The Callers Exact Current Catalog And Settings Revisions")]
    [InlineData("catalog", "execution_catalog_revision_conflict")]
    [InlineData("settings", "execution_settings_revision_conflict")]
    public void RequiresExpectedRevisions(string revision, string reason)
    {
        using var fixture = new PlanBuilderFixture(); var request = fixture.Request(new BindingTarget(fixture.BindingA));
        request = revision == "catalog" ? request with { ExpectedCatalogRevision = request.ExpectedCatalogRevision + 1 }
            : request with { ExpectedSettingsRevision = request.ExpectedSettingsRevision + 1 };
        var before = fixture.Snapshot();
        Assert.Equal(reason, Assert.Throws<ExecutionSelectionException>(() => fixture.Builder.Prepare(request, PlanBuilderFixture.CreatedAt)).Message);
        AssertUnchanged(fixture, before);
    }

    [Theory(DisplayName = "Preparation Requires The Actually Held Lease For Its Own Runtime")]
    [InlineData("foreign")]
    [InlineData("disposed")]
    public void RequiresHeldLease(string lease)
    {
        using var fixture = new PlanBuilderFixture();
        if (lease == "foreign")
        {
            using var other = new PlanBuilderFixture();
            Assert.Equal("runtime_exclusive_lease_required", Assert.Throws<IOException>(() =>
                new ExecutionPlanBuilder(fixture.Instance, fixture.Database, new(), other.Lease, new(5, 6, 7))).Message);
        }
        else
        {
            fixture.Lease.Dispose();
            Assert.Equal("runtime_exclusive_lease_required", Assert.Throws<IOException>(() => fixture.Prepare(new BindingTarget(fixture.BindingA))).Message);
        }
    }

    private static string Id(Guid id) => id.ToString("D");
    private static void AssertUnchanged(PlanBuilderFixture fixture, (string[] Rows, string[] Files) before)
    {
        var after = fixture.Snapshot(); Assert.Equal(before.Rows, after.Rows); Assert.Equal(before.Files, after.Files);
    }
}
