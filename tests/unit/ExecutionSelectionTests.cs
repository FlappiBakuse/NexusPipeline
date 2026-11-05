using System.Collections.Immutable;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Settings;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class ExecutionSelectionTests
{
    [Theory(DisplayName = "Explicit Initialization Ignores Business Quotas But Requires An Enabled Exact Binding")]
    [InlineData(true, ExecutionSelectionDisposition.Candidate)]
    [InlineData(false, ExecutionSelectionDisposition.Skipped)]
    public void SelectsInitialization(bool enabled, ExecutionSelectionDisposition expected)
    {
        var fixture = new ExecutionSelectionFixture();
        var catalog = fixture.Catalog with { Bindings = fixture.Catalog.Bindings.Select(binding =>
            binding.Id == fixture.BindingA ? binding with { IsEnabled = enabled, RunDays = 0, MaximumSuccessfulRunsPerDay = 1 } : binding).ToImmutableArray() };
        var counts = new Dictionary<Guid, int> { [fixture.BindingA] = 3 };
        var selection = ExecutionSelectionPolicy.Select(catalog, fixture.Settings,
            new(new ProjectInitializationTarget(fixture.BindingA), catalog.Revision, fixture.Settings.Revision), counts);
        var item = Assert.Single(selection.Items);
        Assert.Equal(fixture.BindingA, item.Binding.Id);
        Assert.Equal(expected, item.Disposition);
        Assert.Equal(AutomationCompletionAction.None, selection.CompletionAction);
        Assert.Null(selection.Queue);
        Assert.Equal(ExecutionSelectionDisposition.Skipped, Assert.Single(ExecutionSelectionPolicy.Select(catalog, fixture.Settings,
            new(new BindingTarget(fixture.BindingA), catalog.Revision, fixture.Settings.Revision), counts).Items).Disposition);
    }

    [Theory(DisplayName = "Preview Eligibility And Daily Counts Share The Same Frozen Selection")]
    [InlineData(1, ExecutionSelectionDisposition.Candidate)]
    [InlineData(2, ExecutionSelectionDisposition.Skipped)]
    public void FreezesDailyCounts(int successes, ExecutionSelectionDisposition expected)
    {
        var fixture = new ExecutionSelectionFixture();
        var catalog = fixture.Catalog with { Bindings = fixture.Catalog.Bindings.Select(binding =>
            binding.Id == fixture.BindingA ? binding with { MaximumSuccessfulRunsPerDay = 2 } : binding).ToImmutableArray() };
        var counts = new Dictionary<Guid, int> { [fixture.BindingA] = successes };
        var selection = ExecutionSelectionPolicy.Select(catalog, fixture.Settings,
            new(new BindingTarget(fixture.BindingA), catalog.Revision, fixture.Settings.Revision), counts);
        counts[fixture.BindingA] = 99;
        var item = Assert.Single(selection.Items);
        Assert.Equal(successes, item.SuccessfulRunsToday);
        Assert.Equal(2, item.Binding.MaximumSuccessfulRunsPerDay);
        Assert.Equal(expected, item.Disposition);
    }

    [Theory(DisplayName = "Direct Targets Select Only Their Exact Binding Without Expanding The Users Other Scripts")]
    [InlineData("binding")]
    [InlineData("script-user")]
    public void SelectsExactBindings(string target)
    {
        var fixture = new ExecutionSelectionFixture();
        var selection = fixture.Select(target == "binding" ? new BindingTarget(fixture.BindingB) : new ScriptUserTarget(fixture.ScriptId, fixture.UserB));
        var item = Assert.Single(selection.Items);
        Assert.Equal(fixture.BindingB, item.Binding.Id); Assert.Equal(fixture.UserB, item.User.Id); Assert.Equal(fixture.ScriptId, item.Script.Id);
        Assert.Equal(0, item.Ordinal); Assert.Equal(ExecutionSelectionDisposition.Candidate, item.Disposition);
        Assert.Null(selection.Queue); Assert.Equal(AutomationCompletionAction.None, selection.CompletionAction);
    }

    [Fact(DisplayName = "Script Selection Uses Global User Order Then Canonical Identity Rather Than Binding Priority Or Catalog Enumeration")]
    public void PreservesUserOrder()
    {
        var fixture = new ExecutionSelectionFixture();
        var reversed = fixture.Catalog with { Users = fixture.Catalog.Users.Reverse().ToImmutableArray(), Bindings = fixture.Catalog.Bindings.Reverse().ToImmutableArray() };
        var expected = new[] { fixture.UserB, fixture.UserC, fixture.UserA };
        foreach (var source in new[] { fixture.Catalog, reversed })
        {
            var selected = fixture.Select(new ScriptTarget(fixture.ScriptId), source);
            Assert.Equal(expected, selected.Items.Select(item => item.User.Id));
            Assert.Equal(new[] { 9, 1, 0 }, selected.Items.Select(item => item.Binding.Priority));
            Assert.Equal(new[] { 0, 1, 2 }, selected.Items.Select(item => item.Ordinal));
            Assert.DoesNotContain(selected.Items, item => item.Script.Id == fixture.OtherScriptId);
        }
    }

    [Fact]
    public void ExpandsQueueGroups()
    {
        var fixture = new ExecutionSelectionFixture();
        var catalog = fixture.Catalog with { Bindings = fixture.Catalog.Bindings.Select(binding => binding.Id == fixture.OtherBinding
            ? binding with { IsEnabled = false } : binding).ToImmutableArray() };
        var selection = fixture.Select(new QueueTarget(fixture.QueueId), catalog);
        Assert.Equal(new[] { fixture.BindingB, fixture.BindingC, fixture.BindingA }, selection.Items.Select(item => item.Binding.Id));
        Assert.Equal(new[] { 0, 1, 2 }, selection.Items.Select(item => item.Ordinal));
        Assert.DoesNotContain(selection.Items, item => item.Binding.Id == fixture.OtherBinding);
        Assert.Equal(AutomationCompletionAction.Hibernate, selection.CompletionAction);
        var explanation = ExecutionSelectionPolicy.Explain(selection, ExecutionSelectionFixture.Ready(selection));
        Assert.All(explanation.Items, item => Assert.Equal(ExecutionExplanationDisposition.Runnable, item.Disposition));
        Assert.Equal("execution_no_enabled_bindings", explanation.Groups.Single(group => group.ScriptId == fixture.OtherScriptId).ReasonCode);
        Assert.Equal(selection.Items.Select(item => item.Binding.Id), explanation.Items.Select(item => item.Source.Binding.Id));
        Assert.True(explanation.HasRunnable);
        var newcomer = new AutomationUser(Guid.NewGuid(), 1, "New participant", 0);
        var added = new AutomationBinding(Guid.NewGuid(), 1, newcomer.Id, fixture.ScriptId, true, 0);
        var expanded = fixture.Select(new QueueTarget(fixture.QueueId), catalog with { Users = catalog.Users.Add(newcomer), Bindings = catalog.Bindings.Add(added) });
        Assert.Equal(4, expanded.Items.Length); Assert.Equal(newcomer.Id, expanded.Items[0].User.Id);
        Assert.Equal(3, selection.Items.Length);
    }

    [Theory(DisplayName = "Disabled Bindings Remain Skipped And Never Produce Runnable Items Without Changing Their Source")]
    [InlineData("binding")]
    [InlineData("script-user")]
    [InlineData("script")]
    [InlineData("queue")]
    public void SkipsDisabledBindings(string kind)
    {
        var fixture = new ExecutionSelectionFixture(); var catalog = fixture.Disabled();
        ExecutionTarget target = kind switch
        {
            "binding" => new BindingTarget(fixture.BindingB), "script-user" => new ScriptUserTarget(fixture.ScriptId, fixture.UserB),
            "script" => new ScriptTarget(fixture.ScriptId), _ => new QueueTarget(fixture.QueueId)
        };
        var selection = fixture.Select(target, catalog); var explanation = ExecutionSelectionPolicy.Explain(selection, []);
        if (kind == "queue") Assert.Empty(selection.Items); else Assert.NotEmpty(selection.Items);
        Assert.False(selection.HasCandidates); Assert.False(selection.HasBlockedItems);
        Assert.All(selection.Items, item => Assert.Equal(ExecutionSelectionDisposition.Skipped, item.Disposition));
        Assert.All(explanation.Items, item => { Assert.Equal(ExecutionExplanationDisposition.Skipped, item.Disposition); Assert.Equal("execution_binding_disabled", item.ReasonCode); });
        Assert.False(explanation.HasRunnable); Assert.Equal("execution_no_enabled_bindings", explanation.ReasonCode);
        Assert.All(catalog.Bindings, binding => Assert.False(binding.IsEnabled));
    }

    [Theory(DisplayName = "Empty Or Unbound Targets Return An Explicit No Runnable Explanation Without Inventing An Item")]
    [InlineData("script")]
    [InlineData("script-user")]
    [InlineData("queue")]
    public void ExplainsEmptyTargets(string kind)
    {
        var fixture = new ExecutionSelectionFixture();
        var catalog = fixture.Catalog with { Bindings = [], Queues = [fixture.Catalog.Queues[0] with { ScriptIds = [] }] };
        ExecutionTarget target = kind switch { "script" => new ScriptTarget(fixture.ScriptId),
            "script-user" => new ScriptUserTarget(fixture.ScriptId, fixture.UserB), _ => new QueueTarget(fixture.QueueId) };
        var selection = fixture.Select(target, catalog); var explanation = ExecutionSelectionPolicy.Explain(selection, []);
        Assert.Empty(selection.Items); Assert.Empty(explanation.Items); Assert.False(explanation.HasRunnable);
        Assert.Equal(kind == "queue" ? "execution_queue_empty" : "execution_no_enabled_bindings", explanation.ReasonCode);
        Assert.NotEmpty(selection.SourceRevisions);
    }

    [Theory(DisplayName = "Missing Targets Have Distinct Reasons And Cannot Be Reported As Runnable")]
    [InlineData("binding", "execution_binding_missing")]
    [InlineData("script", "execution_script_missing")]
    [InlineData("script-user-script", "execution_script_missing")]
    [InlineData("script-user-user", "execution_user_missing")]
    [InlineData("queue", "execution_queue_missing")]
    public void DistinguishesMissingTargets(string kind, string reason)
    {
        var fixture = new ExecutionSelectionFixture(); var unknown = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        ExecutionTarget target = kind switch
        {
            "binding" => new BindingTarget(unknown), "script" => new ScriptTarget(unknown),
            "script-user-script" => new ScriptUserTarget(unknown, fixture.UserA), "script-user-user" => new ScriptUserTarget(fixture.ScriptId, unknown),
            _ => new QueueTarget(unknown)
        };
        var selection = fixture.Select(target); var result = ExecutionSelectionPolicy.Explain(selection, []);
        Assert.Equal(reason, selection.ReasonCode); Assert.Equal(reason, result.ReasonCode);
        Assert.Empty(selection.Items); Assert.False(result.HasRunnable);
    }

    [Theory(DisplayName = "Invalid Selection Targets Cannot Fall Through Into An Empty Successful Selection")]
    [InlineData("null")]
    [InlineData("binding")]
    [InlineData("script")]
    [InlineData("script-user-script")]
    [InlineData("script-user-user")]
    [InlineData("queue")]
    [InlineData("unsupported")]
    public void RejectsInvalidTargets(string kind)
    {
        var fixture = new ExecutionSelectionFixture();
        ExecutionTarget target = kind switch
        {
            "null" => null!, "binding" => new BindingTarget(Guid.Empty), "script" => new ScriptTarget(Guid.Empty),
            "script-user-script" => new ScriptUserTarget(Guid.Empty, fixture.UserA),
            "script-user-user" => new ScriptUserTarget(fixture.ScriptId, Guid.Empty), "queue" => new QueueTarget(Guid.Empty), _ => new UnsupportedTarget()
        };
        Assert.Equal("execution_selection_request_invalid", Assert.Throws<ExecutionSelectionException>(() => fixture.Select(target)).Message);
    }

    [Theory(DisplayName = "Selection Requires The Exact Expected Catalog And Settings Revisions")]
    [InlineData(true)]
    [InlineData(false)]
    public void RequiresExpectedRevisions(bool catalogChanged)
    {
        var fixture = new ExecutionSelectionFixture();
        var request = new ExecutionSelectionRequest(new BindingTarget(fixture.BindingA), catalogChanged ? fixture.Catalog.Revision - 1 : fixture.Catalog.Revision,
            catalogChanged ? fixture.Settings.Revision : fixture.Settings.Revision + 1);
        Assert.Equal(catalogChanged ? "execution_catalog_revision_conflict" : "execution_settings_revision_conflict",
            Assert.Throws<ExecutionSelectionException>(() => ExecutionSelectionPolicy.Select(fixture.Catalog, fixture.Settings, request)).Message);
    }

    [Fact(DisplayName = "Initial Settings Revision Zero And All Referenced Root Revisions Remain Real Selection Inputs")]
    public void FreezesSourceRevisions()
    {
        var fixture = new ExecutionSelectionFixture(); var selection = fixture.Select(new BindingTarget(fixture.BindingA));
        Assert.Equal(0, selection.SettingsRevision); Assert.Equal(fixture.Catalog.Revision, selection.CatalogRevision);
        Assert.True(selection.HasCandidates); Assert.Equal("execution_inputs_required", selection.ReasonCode);
        var item = Assert.Single(selection.Items);
        Assert.Equal(fixture.Catalog.Roots.Select(root => root.Id).Order(), item.Roots.Select(root => root.Id).Order());
        Assert.Equal(5, item.SourceRevisions.Length);
        Assert.Contains(new ExecutionSelectionRevision(AutomationDefinitionKind.Binding, item.Binding.Id, item.Binding.Revision), item.SourceRevisions);
        Assert.Contains(new ExecutionSelectionRevision(AutomationDefinitionKind.Script, item.Script.Id, item.Script.Revision), item.SourceRevisions);
        Assert.Contains(new ExecutionSelectionRevision(AutomationDefinitionKind.User, item.User.Id, item.User.Revision), item.SourceRevisions);
        Assert.All(item.Roots, root => Assert.Contains(new ExecutionSelectionRevision(AutomationDefinitionKind.Root, root.Id, root.Revision), item.SourceRevisions));
        Assert.Throws<ExecutionSelectionException>(() => ExecutionSelectionPolicy.Explain(selection, []));
        var explanation = ExecutionSelectionPolicy.Explain(selection, ExecutionSelectionFixture.Ready(selection));
        Assert.True(explanation.HasRunnable); Assert.Equal(ExecutionExplanationDisposition.Runnable, Assert.Single(explanation.Items).Disposition);
    }

    [Theory(DisplayName = "Extension Preferences Never Substitute For A Provider Input Assessment")]
    [InlineData("disabled")]
    [InlineData("enabled")]
    [InlineData("absent")]
    public void RequiresProviderAssessment(string preference)
    {
        var fixture = new ExecutionSelectionFixture(); var catalog = fixture.Specialized();
        var settings = fixture.Settings with { Values = fixture.Settings.Values with { Extensions = preference == "absent" ? [] : [new("fixture-extension", preference == "enabled")] } };
        var selection = fixture.Select(new BindingTarget(fixture.BindingA), catalog, settings);
        ImmutableArray<ExecutionItemReadiness> readiness = preference == "disabled" ? [] : [new(selection.Items[0], false, "execution_provider_unavailable")];
        var explanation = ExecutionSelectionPolicy.Explain(selection, readiness);
        Assert.False(explanation.HasRunnable); Assert.Equal(preference != "disabled", explanation.HasBlockedItems);
        Assert.Equal(preference == "disabled" ? "execution_extension_disabled" : "execution_provider_unavailable", explanation.Items[0].ReasonCode);
        Assert.Equal(preference == "disabled" ? ExecutionExplanationDisposition.Skipped : ExecutionExplanationDisposition.Blocked, explanation.Items[0].Disposition);
    }

    [Theory(DisplayName = "Explanation Requires Complete Unique Readiness For Only The Actual Candidate Objects")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("extra")]
    [InlineData("foreign-selection")]
    [InlineData("skipped")]
    [InlineData("blocked")]
    [InlineData("default")]
    [InlineData("invalid-reason")]
    [InlineData("false-verified")]
    [InlineData("true-unavailable")]
    [InlineData("null-readiness")]
    [InlineData("null-item")]
    public void RequiresCandidateReadiness(string fault)
    {
        var fixture = new ExecutionSelectionFixture(); var catalog = fixture.Catalog; var settings = fixture.Settings;
        if (fault == "skipped") catalog = catalog with { Bindings = catalog.Bindings.Select(binding => binding.Id == fixture.BindingA ? binding with { IsEnabled = false } : binding).ToImmutableArray() };
        if (fault == "blocked") { catalog = fixture.Specialized(); settings = settings with { Values = settings.Values with { Extensions = [new("fixture-extension", false)] } }; }
        var selection = fixture.Select(new ScriptTarget(fixture.ScriptId), catalog, settings);
        var ready = ExecutionSelectionFixture.Ready(selection).ToArray();
        ImmutableArray<ExecutionItemReadiness> invalid = fault switch
        {
            "missing" => ready.Skip(1).ToImmutableArray(), "duplicate" => [ready[0], ready[0], ready[2]],
            "extra" => ready.Append(ready[0]).ToImmutableArray(),
            "foreign-selection" => [ready[0] with { Item = fixture.Select(new ScriptTarget(fixture.ScriptId)).Items[0] }, ready[1], ready[2]],
            "skipped" => ready.Append(new(selection.Items.Single(item => item.Disposition == ExecutionSelectionDisposition.Skipped), true, ExecutionSelectionPolicy.InputsVerified)).ToImmutableArray(),
            "blocked" => [new(selection.Items[0], true, ExecutionSelectionPolicy.InputsVerified)], "default" => default,
            "invalid-reason" => [ready[0] with { ReasonCode = "not a reason" }, ready[1], ready[2]],
            "false-verified" => [ready[0] with { IsReady = false }, ready[1], ready[2]],
            "true-unavailable" => [ready[0] with { ReasonCode = "execution_provider_unavailable" }, ready[1], ready[2]],
            "null-readiness" => [null!, ready[1], ready[2]], _ => [ready[0] with { Item = null! }, ready[1], ready[2]]
        };
        Assert.Equal("execution_readiness_unbound", Assert.Throws<ExecutionSelectionException>(() => ExecutionSelectionPolicy.Explain(selection, invalid)).Message);
        Assert.All(ready, item => Assert.Equal(ExecutionSelectionPolicy.InputsVerified, item.ReasonCode));
    }

    [Fact(DisplayName = "Out Of Order Readiness Does Not Reorder Items Or Erase Independent Blocked Reasons")]
    public void PreservesReadinessOrder()
    {
        var fixture = new ExecutionSelectionFixture(); var selection = fixture.Select(new ScriptTarget(fixture.ScriptId));
        var ready = ExecutionSelectionFixture.Ready(selection).Reverse().ToArray();
        ready[1] = ready[1] with { IsReady = false, ReasonCode = "execution_image_changed" };
        var explanation = ExecutionSelectionPolicy.Explain(selection, ready.ToImmutableArray());
        Assert.Equal(selection.Items.ToArray(), explanation.Items.Select(item => item.Source));
        Assert.True(explanation.HasRunnable); Assert.True(explanation.HasBlockedItems);
        Assert.Equal("execution_has_runnable_items", explanation.ReasonCode);
        Assert.Equal(ExecutionExplanationDisposition.Blocked, explanation.Items[1].Disposition);
        Assert.Equal("execution_image_changed", explanation.Items[1].ReasonCode);
    }

    [Fact(DisplayName = "Later Definition Edits Cannot Change Selected Users Arguments Queue Order Or Source Revisions")]
    public void FreezesSelectedDefinitions()
    {
        var fixture = new ExecutionSelectionFixture(); var selected = fixture.Select(new QueueTarget(fixture.QueueId));
        var revisions = selected.SourceRevisions.ToArray(); var original = fixture.Catalog;
        var user = original.Users.Single(item => item.Id == fixture.UserA);
        var script = original.Scripts.Single(item => item.Id == fixture.ScriptId);
        var edits = new AutomationChanges(original.Revision, [], [script with { Arguments = ["--changed"] }], [user with { Name = "Changed", Order = 0 }], [],
            [original.Queues[0] with { ScriptIds = original.Queues[0].ScriptIds.Reverse().ToImmutableArray() }], [], []);
        var next = AutomationEdits.Apply(original, edits); var updated = fixture.Select(new QueueTarget(fixture.QueueId), next);
        Assert.Equal(original.Queues[0].ScriptIds.ToArray(), selected.Items.Select(item => item.Script.Id).Distinct());
        Assert.Equal(next.Queues[0].ScriptIds.ToArray(), updated.Items.Select(item => item.Script.Id).Distinct());
        Assert.Equal("Same name", selected.Items.Single(item => item.Binding.Id == fixture.BindingA).User.Name);
        Assert.Equal(2, selected.Items.Single(item => item.Binding.Id == fixture.BindingA).User.Order);
        Assert.Equal("--frozen", selected.Items[0].Script.Arguments[0]); Assert.Equal(revisions, selected.SourceRevisions.ToArray());
        Assert.Contains(updated.Items, item => item.User.Id == fixture.UserA && item.User.Name == "Changed" && item.User.Order == 0);
        Assert.Contains(updated.Items, item => item.Script.Id == fixture.ScriptId && item.Script.Arguments[0] == "--changed");
    }

    private sealed record UnsupportedTarget : ExecutionTarget;
}
