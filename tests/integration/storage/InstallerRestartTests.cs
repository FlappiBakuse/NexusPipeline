using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Installation;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class InstallerRestartTests
{
    [Theory]
    [InlineData("prepared")]
    [InlineData("payload")]
    [InlineData("target")]
    [InlineData("swapped")]
    [InlineData("planned")]
    [InlineData("published")]
    [InlineData("observed")]
    [InlineData("committed")]
    public void RecoversStoppedTransition(string stage)
    {
        using var fixture = new InstallerOwnedTests.OwnedFixture(initialized: true);
        var frame = Prepare(fixture, stage);
        StopActors(fixture);
        fixture.Product.Owner.RecoverTransition(frame.Root, InstallerRetainedFixture.Parent(), Work(fixture));
        var snapshot = ApplicationTree.Capture(fixture.Scanner, frame.Root, default);
        var committed = stage is "observed" or "committed";
        var expected = committed ? frame.TargetApplication!.Objects : frame.OwnedSource!.ApplicationObjects;
        Assert.True(expected.Where(item => item.RelativePath.Length > 0).SequenceEqual(
            PhysicalTreeIdentity.Capture(frame.Root, snapshot).Where(item => item.RelativePath.Length > 0)));
        Assert.Equal(fixture.Identity, new InstallationAnchor(fixture.Product.Paths.ManagerRoot, fixture.Product.Paths.RegistryKey).Read(frame.Root));
        Assert.False(Directory.Exists(fixture.Product.Checkpoint));
        Assert.False(Directory.Exists(InstallerPayloadTree.Workspace(frame)));
        fixture.Product.Owner.RecoverTransition(frame.Root, InstallerRetainedFixture.Parent(), Work(fixture));
    }

    [Fact]
    public async Task RecoversThroughEntry()
    {
        using var fixture = new InstallerOwnedTests.OwnedFixture(initialized: true);
        var frame = Prepare(fixture, "published");
        StopActors(fixture);
        var result = await fixture.Product.Execute("installer-state", "recover-transition", "--root", frame.Root, "--work-root", Work(fixture));
        Assert.Equal(0, result.ExitCode);
        Assert.False(Directory.Exists(fixture.Product.Checkpoint));
        Assert.True(frame.OwnedSource!.ApplicationObjects.SequenceEqual(PhysicalTreeIdentity.Capture(frame.Root,
            ApplicationTree.Capture(fixture.Scanner, frame.Root, default))));
    }

    [Fact]
    public void PreservesLiveTransition()
    {
        using var fixture = new InstallerOwnedTests.OwnedFixture(initialized: true);
        var frame = Prepare(fixture, "swapped");
        var before = fixture.Scanner.Capture(fixture.Product.Checkpoint, default);
        Assert.Throws<IOException>(() => fixture.Product.Owner.RecoverTransition(frame.Root, InstallerRetainedFixture.Parent(), Work(fixture)));
        Assert.True(before.Entries.SequenceEqual(fixture.Scanner.Capture(fixture.Product.Checkpoint, default).Entries));
        Assert.True(Directory.Exists(InstallerPayloadTree.Workspace(frame)));
    }

    [Fact]
    public void RejectsOverlappingInspection()
    {
        using var fixture = new InstallerOwnedTests.OwnedFixture(initialized: true);
        var frame = Prepare(fixture, "swapped");
        StopActors(fixture);
        var before = fixture.Scanner.Capture(fixture.Product.Checkpoint, default);
        Assert.Throws<IOException>(() => fixture.Product.Owner.RecoverTransition(frame.Root, InstallerRetainedFixture.Parent(),
            fixture.Product.Paths.ManagerRoot));
        Assert.True(before.Entries.SequenceEqual(fixture.Scanner.Capture(fixture.Product.Checkpoint, default).Entries));
    }

    [Fact]
    public void PreservesForeignRecovery()
    {
        using var fixture = new InstallerOwnedTests.OwnedFixture(initialized: true);
        var frame = Prepare(fixture, "swapped");
        StopActors(fixture);
        var path = Path.Combine(frame.Root, "README.md");
        File.AppendAllText(path, "Unknown user bytes");
        var bytes = File.ReadAllBytes(path);
        Assert.Throws<IOException>(() => fixture.Product.Owner.RecoverTransition(frame.Root, InstallerRetainedFixture.Parent(), Work(fixture)));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.True(Directory.Exists(fixture.Product.Checkpoint));
        Assert.True(Directory.Exists(InstallerPayloadTree.Workspace(frame)));
    }

    [Fact]
    public void RecoversCommittedCheckpoint()
    {
        using var fixture = new InstallerOwnedTests.OwnedFixture(initialized: true);
        var frame = Prepare(fixture, "committed");
        StopActors(fixture);
        foreach (var stage in new[] { "prepared", "payload", "target", "planned", "observed" })
            File.Delete(Path.Combine(fixture.Product.Checkpoint, stage + ".protected"));
        var saved = Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("NEXUS_TEST_ROOT", fixture.Product.Root);
            Assert.Throws<IOException>(() => InstanceLease.Acquire(fixture.Runtime, new()));
        }
        finally { Environment.SetEnvironmentVariable("NEXUS_TEST_ROOT", saved); }
        fixture.Product.Owner.RecoverTransition(frame.Root, InstallerRetainedFixture.Parent(), Work(fixture));
        var objects = PhysicalTreeIdentity.Capture(frame.Root, ApplicationTree.Capture(fixture.Scanner, frame.Root, default));
        Assert.True(frame.TargetApplication!.Objects.Where(item => item.RelativePath.Length > 0)
            .SequenceEqual(objects.Where(item => item.RelativePath.Length > 0)));
        Assert.False(Directory.Exists(fixture.Product.Checkpoint));
        Assert.False(Directory.Exists(InstallerPayloadTree.Workspace(frame)));
    }

    private static InstallerCheckpoint Prepare(InstallerOwnedTests.OwnedFixture fixture, string stage)
    {
        using var gate = InstallerRuntimeGate.Acquire(fixture.Product.Application);
        if (stage == "prepared") return fixture.Product.Owner.BeginTransition(fixture.Product.Application, fixture.Product.Transaction,
            fixture.Product.Target, InstallerRetainedFixture.Parent(), Work(fixture));
        if (stage == "payload") return InstallerExchangeTests.Preserve(fixture);
        var payload = InstallerExchangeTests.Payload(fixture);
        if (stage is "target" or "swapped")
        {
            var target = InstallerExchangeTests.Freeze(fixture, payload);
            if (stage == "swapped") InstallerExchangeTests.MoveTargets(target, 4);
            return target;
        }
        var frame = InstallerExchangeTests.PublishFrame(fixture, payload, stage != "planned", stage is "observed" or "committed");
        if (stage == "committed") new InstallerCheckpointStore(fixture.Product.Paths, "test").CommitTransition(frame);
        return frame;
    }

    private static void StopActors(InstallerOwnedTests.OwnedFixture fixture)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec")!)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true
        };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("set /p nexus_wait=");
        using var process = Process.Start(start) ?? throw new IOException("installer_actor_start_failed");
        var actor = ProcessIdentityReader.Read(process.Id);
        process.StandardInput.Close();
        Assert.True(process.WaitForExit(10000));
        string? parent = null;
        foreach (var name in new[] { "prepared", "payload", "target", "planned", "observed", "committed" })
        {
            var path = Path.Combine(fixture.Product.Checkpoint, name + ".protected");
            if (!File.Exists(path)) continue;
            var frame = InstallerCheckpointCodec.Unseal(File.ReadAllBytes(path));
            var bytes = InstallerCheckpointCodec.Seal(frame with
            {
                SetupOwner = actor, Writer = actor, OwnedSource = frame.OwnedSource! with { SetupOwner = actor },
                ParentSha256 = name == "committed" ? frame.ParentSha256 : parent
            });
            File.WriteAllBytes(path, bytes);
            parent = Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
    }

    private static string Work(InstallerOwnedTests.OwnedFixture fixture) => Path.Combine(fixture.Product.Root, "restart-inspection");
}
