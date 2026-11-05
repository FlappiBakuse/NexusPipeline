using System.Runtime.Versioning;
using NexusPipeline.Platform.Storage;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ApplicationTreeTests
{
    [Fact]
    public void CapturesOwnedFiles()
    {
        var fixture = new ApplicationTreeFixture();
        Assert.DoesNotContain(fixture.Plan.Before.Snapshot.Entries, item =>
            item.RelativePath.StartsWith("runtime", StringComparison.Ordinal) ||
            item.RelativePath.StartsWith("plugins", StringComparison.Ordinal) || item.RelativePath == "notes.txt");
        Assert.Equal(fixture.Plan.Before.OriginalIdentity, PhysicalObjectIdentity.Read(fixture.Target));
        using var held = TreeReadLease.AcquireApplication(fixture.Target, fixture.Plan.ParentIdentity, fixture.Plan.Before);
        File.WriteAllText(Path.Combine(fixture.Target, "runtime", "user.txt"), "changed-user");
        using var borrowed = held.Borrow();
        Assert.Throws<IOException>(() => File.WriteAllText(Path.Combine(fixture.Target, "NexusPipeline.exe"), "changed-app"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SwitchesOwnedRoots(bool missingReadme)
    {
        var fixture = new ApplicationTreeFixture(missingReadme);
        fixture.Materialize(); fixture.Preserve();
        fixture.Effects.PreserveOriginal(fixture.Plan, fixture.Saved with { Stage = "original-preservation-started" });
        fixture.Install();
        fixture.Effects.InstallCandidate(fixture.Plan, fixture.Saved with { Stage = "original-preserved" });
        Assert.Equal("candidate-host", File.ReadAllText(Path.Combine(fixture.Target, "NexusPipeline.exe")));
        AssertProtected(fixture);
        using (fixture.Effects.HoldInstalled(fixture.Plan, fixture.Saved))
        {
            Assert.Throws<IOException>(() => File.WriteAllText(Path.Combine(fixture.Target, "NexusPipeline.exe"), "changed"));
            File.WriteAllText(Path.Combine(fixture.Target, "notes.txt"), "still-user-owned");
        }
        Assert.Equal((true, true), fixture.Effects.ObserveRestoration(fixture.Plan, fixture.Saved));
        fixture.Effects.RetireCandidate(fixture.Plan, fixture.Saved);
        fixture.Effects.RestoreOriginal(fixture.Plan, fixture.Saved);
        Assert.Equal("original-host", File.ReadAllText(Path.Combine(fixture.Target, "NexusPipeline.exe")));
        Assert.Equal(!missingReadme, File.Exists(Path.Combine(fixture.Target, "README.md")));
        Assert.Equal(fixture.Plan.Before.OriginalIdentity, PhysicalObjectIdentity.Read(fixture.Target));
        Assert.Equal("still-user-owned", File.ReadAllText(Path.Combine(fixture.Target, "notes.txt")));
        using var proof = fixture.Effects.HoldRestored(fixture.Plan, fixture.Saved);
        proof.Require(fixture.Plan, fixture.Saved);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 3)]
    [InlineData(true, 1)]
    [InlineData(true, 3)]
    public void RestoresPartialSwitch(bool installed, int count)
    {
        var fixture = new ApplicationTreeFixture(); fixture.Materialize();
        if (installed) { fixture.Preserve(); fixture.PartialInstall(count); }
        else fixture.PartialPreserve(count);
        Assert.Equal((installed, true), fixture.Effects.ObserveRestoration(fixture.Plan, fixture.Saved));
        if (installed) fixture.Effects.RetireCandidate(fixture.Plan, fixture.Saved);
        fixture.Effects.RestoreOriginal(fixture.Plan, fixture.Saved);
        fixture.Effects.RetireCandidate(fixture.Plan, fixture.Saved);
        fixture.Effects.RestoreOriginal(fixture.Plan, fixture.Saved);
        Assert.Equal((false, false), fixture.Effects.ObserveRestoration(fixture.Plan, fixture.Saved));
        Assert.Equal("original-host", File.ReadAllText(Path.Combine(fixture.Target, "NexusPipeline.exe")));
        AssertProtected(fixture);
        using var proof = fixture.Effects.HoldRestored(fixture.Plan, fixture.Saved);
        proof.Require(fixture.Plan, fixture.Saved);
    }

    [Theory]
    [InlineData("workspace")]
    [InlineData("original")]
    [InlineData("candidate")]
    public void RejectsUnknownBytes(string location)
    {
        var fixture = new ApplicationTreeFixture(); fixture.Materialize();
        var directory = location switch
        {
            "original" => Path.Combine(fixture.Target, "resources"),
            "candidate" => Path.Combine(fixture.Work, "candidate", "resources"),
            _ => fixture.Work
        };
        var unknown = Path.Combine(directory, "unknown.txt"); File.WriteAllText(unknown, "preserved unknown bytes");
        Assert.Throws<IOException>(() => fixture.Effects.PreserveOriginal(fixture.Plan,
            fixture.Saved with { Stage = "original-preservation-started" }));
        Assert.Equal("preserved unknown bytes", File.ReadAllText(unknown));
        AssertProtected(fixture);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsReplacedIdentity(bool directory)
    {
        var fixture = new ApplicationTreeFixture();
        var replaced = Path.Combine(fixture.Root, "replaced");
        if (directory)
        {
            Directory.Move(fixture.Target, replaced);
            ApplicationTreeFixture.WritePayload(fixture.Target, "original");
        }
        else
        {
            var file = Path.Combine(fixture.Target, "NexusPipeline.exe");
            File.Move(file, replaced); File.WriteAllText(file, "original-host");
        }
        Assert.Throws<IOException>(() => fixture.Effects.CreateWorkspace(fixture.Plan));
        Assert.False(Directory.Exists(fixture.Work));
        Assert.Equal("original-host", File.ReadAllText(Path.Combine(fixture.Target, "NexusPipeline.exe")));
        Assert.Equal("original-host", File.ReadAllText(directory ? Path.Combine(replaced, "NexusPipeline.exe") : replaced));
    }

    private static void AssertProtected(ApplicationTreeFixture fixture)
    {
        Assert.Equal(fixture.Plan.Before.OriginalIdentity, PhysicalObjectIdentity.Read(fixture.Target));
        Assert.Equal("runtime-user", File.ReadAllText(Path.Combine(fixture.Target, "runtime", "user.txt")));
        Assert.Equal("plugin-user", File.ReadAllText(Path.Combine(fixture.Target, "plugins", "user-plugin", "state.txt")));
        Assert.Equal("unrelated-user", File.ReadAllText(Path.Combine(fixture.Target, "notes.txt")));
    }
}
