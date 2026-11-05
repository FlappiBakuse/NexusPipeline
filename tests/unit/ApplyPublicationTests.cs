using NexusPipeline.Core.Extensions;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class ApplyPublicationTests
{
    [Fact(DisplayName = "A Completed Callback Requests Publication Without Claiming Enabled Or Final Completion")]
    public void DefersScopePublication()
    {
        var plan = ExtensionApplyPolicy.Freeze(new(ExtensionApplyAction.Install, "fixture-plugin", 0, Package()),
            null, true, ExtensionObservedActivity.None);
        var decision = ApplyCompletionPolicy.Decide(plan, new(ExtensionPayloadObservation.CandidateInstalled,
            ExtensionScopeClosure.NotRequired, ExtensionActivationObservation.PreparedForPublication));
        Assert.Equal(ExtensionApplyDisposition.WaitForPublication, decision.Disposition);
        Assert.Equal(true, decision.PayloadIsInstalled); Assert.False(decision.IsEnabled);
        Assert.Equal("extension_activation_publication_pending", decision.ReasonCode);
        var published = ApplyCompletionPolicy.Decide(plan, new(ExtensionPayloadObservation.CandidateInstalled,
            ExtensionScopeClosure.NotRequired, ExtensionActivationObservation.Published));
        Assert.Equal(ExtensionApplyDisposition.CommitInstalled, published.Disposition); Assert.True(published.IsEnabled);
    }

    [Fact(DisplayName = "Prepared Publication Does Not Replace The Actual Previous Scope Closure")]
    public void KeepsPreviousClosure()
    {
        var original = new ExtensionInstalledRevision(4, true, Package());
        var plan = ExtensionApplyPolicy.Freeze(new(ExtensionApplyAction.Upgrade, "fixture-plugin", 4, Package()),
            original, true, ExtensionObservedActivity.Active);
        var decision = ApplyCompletionPolicy.Decide(plan, new(ExtensionPayloadObservation.CandidateInstalled,
            ExtensionScopeClosure.Unknown, ExtensionActivationObservation.PreparedForPublication));
        Assert.Equal(ExtensionApplyDisposition.Unverified, decision.Disposition);
        Assert.Null(decision.PayloadIsInstalled); Assert.False(decision.IsEnabled);
    }

    [Theory(DisplayName = "Disabled Or Removed Payload Cannot Claim An Unrequested Prepared Activation")]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsUnrequestedActivation(bool remove)
    {
        var original = new ExtensionInstalledRevision(4, true, Package());
        var plan = ExtensionApplyPolicy.Freeze(new(remove ? ExtensionApplyAction.Remove : ExtensionApplyAction.Upgrade,
            "fixture-plugin", 4, remove ? null : Package()), original, false, ExtensionObservedActivity.None);
        var decision = ApplyCompletionPolicy.Decide(plan, new(remove ? ExtensionPayloadObservation.OwnedTargetRemoved :
            ExtensionPayloadObservation.CandidateInstalled, ExtensionScopeClosure.NotRequired, ExtensionActivationObservation.PreparedForPublication));
        Assert.Equal(ExtensionApplyDisposition.Unverified, decision.Disposition); Assert.False(decision.IsEnabled);
    }

    private static ExtensionPackageIdentity Package() => new("fixture-plugin", "0.5.0",
        ExtensionPackageKind.ManagedCode, new string('a', 64), new string('b', 64));
}
