using NexusPipeline.Modules.Plugins.Repository;
using NexusPipeline.Modules.Plugins.Runtime;
using Xunit;

namespace NexusPipeline.Tests.Plugins;

public sealed class PluginUpdatePolicyTests
{
    private static readonly PluginRepositorySourceContext Stable = PluginRepositorySourceContext.Stable;
    private static readonly PluginRepositorySourceContext Develop = PluginRepositorySourceContext.ForChannel("develop");

    [Fact]
    public void Evaluate_CoversAdmissionAndOwnershipFailures()
    {
        PluginCatalogEntry entry = Entry("0.2.0", Stable, new string('b', 64));
        PluginSummary installed = Installed("0.1.0");
        PluginOwnership owner = Owned("0.1.0", new string('a', 64));

        AssertDecision(PluginUpdateDecisionKind.BlockedPending, false,
            PluginUpdatePolicy.Evaluate(installed, owner, entry, Stable, new PluginPendingOperation()));
        AssertDecision(PluginUpdateDecisionKind.Install, true,
            PluginUpdatePolicy.Evaluate(null, null, entry, Stable, null));
        AssertDecision(PluginUpdateDecisionKind.Unmanaged, false,
            PluginUpdatePolicy.Evaluate(installed, null, entry, Stable, null));
        AssertDecision(PluginUpdateDecisionKind.ArtifactMismatch, false,
            PluginUpdatePolicy.Evaluate(installed with { ArtifactName = "OtherArtifact" }, owner, entry, Stable, null));
        AssertDecision(PluginUpdateDecisionKind.OwnershipMismatch, false,
            PluginUpdatePolicy.Evaluate(installed, Owned("0.0.9", new string('a', 64)), entry, Stable, null));
    }

    [Fact]
    public void Evaluate_CoversCompatibilityAndVersionDecisions()
    {
        PluginSummary installed = Installed("0.1.0");
        PluginOwnership owner = Owned("0.1.0", new string('a', 64));

        AssertDecision(PluginUpdateDecisionKind.Incompatible, false,
            PluginUpdatePolicy.Evaluate(
                installed,
                owner,
                Entry("0.2.0", Stable, new string('b', 64)) with { MinHostVersion = "999.0.0" },
                Stable,
                null));
        AssertDecision(PluginUpdateDecisionKind.Upgrade, true,
            PluginUpdatePolicy.Evaluate(installed, owner, Entry("0.2.0", Stable, new string('b', 64)), Stable, null));
        AssertDecision(PluginUpdateDecisionKind.VersionAhead, false,
            PluginUpdatePolicy.Evaluate(installed with { Version = "0.3.0" }, Owned("0.3.0", new string('a', 64)), Entry("0.2.0", Stable, new string('b', 64)), Stable, null));
    }

    [Fact]
    public void Evaluate_CoversSameVersionPreviewAndStableImmutability()
    {
        string installedHash = new string('a', 64);
        PluginSummary installed = Installed("0.1.0");

        AssertDecision(PluginUpdateDecisionKind.NoChange, false,
            PluginUpdatePolicy.Evaluate(installed, Owned("0.1.0", installedHash), Entry("0.1.0", Stable, installedHash), Stable, null));
        AssertDecision(PluginUpdateDecisionKind.RefreshPreview, true,
            PluginUpdatePolicy.Evaluate(installed, Owned("0.1.0", installedHash), Entry("0.1.0", Develop, new string('b', 64)), Develop, null));
        AssertDecision(PluginUpdateDecisionKind.ReplacePreviewWithStable, true,
            PluginUpdatePolicy.Evaluate(installed, Owned("0.1.0", installedHash, "develop", new string('c', 40)), Entry("0.1.0", Stable, new string('b', 64)), Stable, null));
        AssertDecision(PluginUpdateDecisionKind.StableContentConflict, false,
            PluginUpdatePolicy.Evaluate(installed, Owned("0.1.0", installedHash), Entry("0.1.0", Stable, new string('b', 64)), Stable, null));
        AssertDecision(PluginUpdateDecisionKind.ChannelMismatch, false,
            PluginUpdatePolicy.Evaluate(installed, Owned("0.1.0", installedHash), Entry("0.1.0", Develop, new string('b', 64)), Stable, null));
    }

    [Fact]
    public void Candidate_FreezesPackageIdentityAndCatalogEntry()
    {
        PluginCatalogEntry entry = Entry("0.2.0", Develop, new string('b', 64)) with
        {
            Capabilities = new[] { "emulator" },
        };

        PluginPackageCandidate candidate = PluginPackageCandidate.FromEntry(entry, Develop);

        Assert.Equal(entry.Name, candidate.Name);
        Assert.Equal(entry.PackageUrl, candidate.PackageUrl);
        Assert.Equal(entry.SourceCommit, candidate.SourceCommit);
        Assert.NotSame(entry.Capabilities, candidate.FrozenEntry.Capabilities);
        Assert.Equal(entry.Capabilities, candidate.FrozenEntry.Capabilities);
    }

    private static void AssertDecision(
        PluginUpdateDecisionKind kind,
        bool canStage,
        PluginUpdateDecision decision)
    {
        Assert.Equal(kind, decision.Kind);
        Assert.Equal(canStage, decision.CanStage);
    }

    private static PluginSummary Installed(string version) => new(
        "fixture",
        "FixtureArtifact",
        "Fixture",
        "测试",
        "",
        version,
        "data-specialized",
        "",
        Array.Empty<string>(),
        false,
        "");

    private static PluginOwnership Owned(
        string version,
        string sha256,
        string channel = "stable",
        string sourceCommit = "") => new()
    {
        Name = "fixture",
        ArtifactName = "FixtureArtifact",
        Version = version,
        Kind = "data-specialized",
        Sha256 = sha256,
        Channel = channel,
        SourceCommit = sourceCommit,
    };

    private static PluginCatalogEntry Entry(
        string version,
        PluginRepositorySourceContext source,
        string sha256) => new(
        "fixture",
        "Fixture",
        "测试",
        "",
        version,
        "data-specialized",
        "",
        Array.Empty<string>(),
        "0.0.0",
        source.IsDevelop
            ? $"https://github.com/FlappiBakuse/NexusPipeline-Plugins/releases/download/plugins-develop/FixtureArtifact-{version}-{sha256}.zip"
            : $"https://raw.githubusercontent.com/FlappiBakuse/NexusPipeline-Plugins/main/packages/FixtureArtifact/FixtureArtifact-{version}.zip",
        sha256,
        1)
    {
        ArtifactName = "FixtureArtifact",
        Channel = source.Channel,
        SourceCommit = source.IsDevelop ? new string('c', 40) : "",
    };
}
