using NexusPipeline.Core.Extensions;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class ExtensionApplyTests
{
    [Theory(DisplayName = "An Installed Version Cannot Be Republished With Different Original Content")]
    [InlineData("archive")]
    [InlineData("manifest")]
    public void BindsOriginalContent(string changed)
    {
        var current = Original();
        var candidate = current.Package with
        {
            ArchiveSha256 = changed == "archive" ? new string('c', 64) : current.Package.ArchiveSha256,
            ManifestSha256 = changed == "manifest" ? new string('d', 64) : current.Package.ManifestSha256
        };
        var error = Assert.Throws<ExtensionApplyException>(() => ExtensionApplyPolicy.Freeze(
            new(ExtensionApplyAction.Upgrade, "fixture-plugin", 7, candidate), current, false, ExtensionObservedActivity.None));
        Assert.Equal("extension_same_version_content_changed", error.ReasonCode);
    }

    [Theory(DisplayName = "Disabled Installation And Upgrade Commit Installed Without Requesting Activation")]
    [InlineData(false)]
    [InlineData(true)]
    public void PreservesDisabledInstalls(bool upgrade)
    {
        var original = upgrade ? Original() : null;
        var candidate = Package("0.5.1");
        var plan = ExtensionApplyPolicy.Freeze(new(upgrade ? ExtensionApplyAction.Upgrade : ExtensionApplyAction.Install,
            "fixture-plugin", original?.Revision ?? 0, candidate), original, false, ExtensionObservedActivity.None);
        Assert.False(plan.MustActivateCandidate);
        Assert.False(plan.MustDeactivateExistingScope);
        var decision = ApplyCompletionPolicy.Decide(plan,
            new(ExtensionPayloadObservation.CandidateInstalled, ExtensionScopeClosure.NotRequired, ExtensionActivationObservation.NotAttempted));
        Assert.Equal(ExtensionApplyDisposition.CommitInstalled, decision.Disposition);
        Assert.Equal(true, decision.PayloadIsInstalled); Assert.False(decision.IsEnabled);
        Assert.False(plan.DesiredEnabled);
    }

    [Theory(DisplayName = "Removal Only Requests Closure For An Actually Observed Existing Scope")]
    [InlineData(false)]
    [InlineData(true)]
    public void ClosesObservedScopes(bool active)
    {
        var plan = ExtensionApplyPolicy.Freeze(new(ExtensionApplyAction.Remove, "fixture-plugin", 7, null),
            Original(), true, active ? ExtensionObservedActivity.Active : ExtensionObservedActivity.None);
        Assert.Equal(active, plan.MustDeactivateExistingScope); Assert.False(plan.MustActivateCandidate);
        var decision = ApplyCompletionPolicy.Decide(plan,
            new(ExtensionPayloadObservation.OwnedTargetRemoved, active ? ExtensionScopeClosure.Stopped : ExtensionScopeClosure.NotRequired,
                ExtensionActivationObservation.NotAttempted));
        Assert.Equal(ExtensionApplyDisposition.CommitRemoved, decision.Disposition);
        Assert.Equal(false, decision.PayloadIsInstalled); Assert.False(decision.IsEnabled);
        Assert.True(plan.DesiredEnabled);
    }

    [Theory(DisplayName = "Enabled Preference Cannot Stand In For Actual Activation")]
    [InlineData(ExtensionActivationObservation.Published, ExtensionApplyDisposition.CommitInstalled, true)]
    [InlineData(ExtensionActivationObservation.PendingRestart, ExtensionApplyDisposition.WaitForRestart, false)]
    [InlineData(ExtensionActivationObservation.NotAttempted, ExtensionApplyDisposition.Unverified, false)]
    [InlineData(ExtensionActivationObservation.Failed, ExtensionApplyDisposition.Failed, false)]
    [InlineData(ExtensionActivationObservation.Unknown, ExtensionApplyDisposition.Unverified, false)]
    public void RequiresActualActivation(ExtensionActivationObservation activation,
        ExtensionApplyDisposition expected, bool isEnabled)
    {
        var plan = ExtensionApplyPolicy.Freeze(new(ExtensionApplyAction.Upgrade, "fixture-plugin", 7, Package("0.5.1")),
            Original(), true, ExtensionObservedActivity.Active);
        var decision = ApplyCompletionPolicy.Decide(plan,
            new(ExtensionPayloadObservation.CandidateInstalled, ExtensionScopeClosure.Stopped, activation));
        Assert.Equal(expected, decision.Disposition);
        Assert.Equal(true, decision.PayloadIsInstalled); Assert.Equal(isEnabled, decision.IsEnabled);
    }

    [Theory(DisplayName = "Unknown Scope Closure Cannot Be Hidden By An Apparently Completed Physical Change")]
    [InlineData(ExtensionPayloadObservation.CandidateInstalled)]
    [InlineData(ExtensionPayloadObservation.OwnedTargetRemoved)]
    public void RejectsUnknownClosure(ExtensionPayloadObservation payload)
    {
        var action = payload == ExtensionPayloadObservation.CandidateInstalled ? ExtensionApplyAction.Upgrade : ExtensionApplyAction.Remove;
        var plan = ExtensionApplyPolicy.Freeze(new(action, "fixture-plugin", 7, action == ExtensionApplyAction.Upgrade ? Package("0.5.1") : null),
            Original(), false, ExtensionObservedActivity.Active);
        var decision = ApplyCompletionPolicy.Decide(plan,
            new(payload, ExtensionScopeClosure.Unknown, ExtensionActivationObservation.NotAttempted));
        Assert.Equal(ExtensionApplyDisposition.Unverified, decision.Disposition);
        Assert.Null(decision.PayloadIsInstalled); Assert.False(decision.IsEnabled);
    }

    [Fact(DisplayName = "Unknown Activity And Stale Installed Revisions Never Become Apply Plans")]
    public void RejectsStalePlans()
    {
        Assert.Equal("extension_activity_unverified", Assert.Throws<ExtensionApplyException>(() => ExtensionApplyPolicy.Freeze(
            new(ExtensionApplyAction.Upgrade, "fixture-plugin", 7, Package("0.5.1")), Original(), false,
            ExtensionObservedActivity.Unknown)).ReasonCode);
        Assert.Equal("extension_installed_revision_conflict", Assert.Throws<ExtensionApplyException>(() => ExtensionApplyPolicy.Freeze(
            new(ExtensionApplyAction.Remove, "fixture-plugin", 6, null), Original(), false,
            ExtensionObservedActivity.None)).ReasonCode);
    }

    private static ExtensionPackageIdentity Package(string version = "0.5.0") =>
        new("fixture-plugin", version, ExtensionPackageKind.ManagedCode, new string('a', 64), new string('b', 64));
    private static ExtensionInstalledRevision Original() => new(7, true, Package());
}
