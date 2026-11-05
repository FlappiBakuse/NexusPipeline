using NexusPipeline.Core.Configuration;
using System.Runtime.Versioning;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class PayloadPlanTests
{
    [Theory(DisplayName = "Payload Intent Freezes Exact Action Trees Resources And Original Objects Without Changing The Site")]
    [InlineData(JournalKind.UpdateApply, PayloadAction.Upgrade)]
    [InlineData(JournalKind.ExtensionApply, PayloadAction.Install)]
    [InlineData(JournalKind.ExtensionApply, PayloadAction.Upgrade)]
    [InlineData(JournalKind.ExtensionApply, PayloadAction.Remove)]
    public void FreezesPayloadIntent(JournalKind kind, PayloadAction action)
    {
        using var fixture = PayloadTestFixture.Create(kind, action);
        var before = fixture.Scanner.Capture(fixture.Plan.TargetPath, default);
        using var prepared = fixture.Codec.Freeze(fixture.Plan);
        var restored = fixture.Codec.Read(prepared.Reference);
        Assert.Equal(kind, restored.Kind);
        Assert.Equal(action, restored.Action);
        Assert.Equal(fixture.Plan.Before.Snapshot.Entries, restored.Before.Snapshot.Entries);
        Assert.Equal(fixture.Plan.Before.OriginalObjects, restored.Before.OriginalObjects);
        Assert.Equal(fixture.Plan.Candidate.Snapshot.Entries, restored.Candidate.Snapshot.Entries);
        Assert.Equal(fixture.Plan.ResourceKeys, restored.ResourceKeys);
        Assert.Equal(before.Entries, fixture.Scanner.Capture(fixture.Plan.TargetPath, default).Entries);
        Assert.Equal(before.Shape, fixture.Scanner.Capture(fixture.Plan.TargetPath, default).Shape);
        Assert.Throws<IOException>(() => File.WriteAllBytes(Path.Combine(fixture.Instance.RuntimeRoot, prepared.Reference.RelativePath), [0]));
    }

    [Theory(DisplayName = "Unbound Payload Plans Are Rejected Before Plan Publication Or Target Mutation")]
    [InlineData("instance")]
    [InlineData("kind")]
    [InlineData("operation")]
    [InlineData("journal")]
    [InlineData("target")]
    [InlineData("resource")]
    [InlineData("missing-identity")]
    [InlineData("cross-volume")]
    [InlineData("update-remove")]
    [InlineData("missing-extension-id")]
    [InlineData("install-existing")]
    public void RejectsUnboundIntent(string fault)
    {
        using var fixture = PayloadTestFixture.Create(JournalKind.ExtensionApply, PayloadAction.Upgrade);
        var plan = fixture.Plan;
        var changed = fault switch
        {
            "instance" => plan with { InstanceId = Guid.NewGuid() },
            "kind" => plan with { Kind = JournalKind.RunAttempt },
            "operation" => plan with { OperationId = Guid.Empty },
            "journal" => plan with { JournalId = Guid.Empty },
            "target" => plan with { TargetPath = fixture.Root },
            "resource" => plan with { ResourceKeys = ["maintenance:host-payload"] },
            "missing-identity" => plan with { Before = plan.Before with { OriginalObjects = [] } },
            "cross-volume" => plan with { ParentIdentity = plan.ParentIdentity with { VolumeSerial = "ffffffffffffffff" } },
            "update-remove" => plan with { Kind = JournalKind.UpdateApply, Action = PayloadAction.Remove,
                ExtensionId = null, TargetPath = fixture.Roots.HostPayloadRoot, ResourceKeys = ["maintenance:host-payload"] },
            "missing-extension-id" => plan with { ExtensionId = null },
            "install-existing" => plan with { Action = PayloadAction.Install },
            _ => throw new InvalidOperationException()
        };
        var before = fixture.Scanner.Capture(plan.TargetPath, default);
        var plans = Path.Combine(fixture.Instance.RuntimeRoot, "blobs", "plans");
        var existing = Directory.EnumerateFiles(plans).Order(StringComparer.Ordinal).ToArray();
        Assert.ThrowsAny<Exception>(() => fixture.Codec.Freeze(changed));
        Assert.Equal(before.Entries, fixture.Scanner.Capture(plan.TargetPath, default).Entries);
        Assert.Equal(existing, Directory.EnumerateFiles(plans).Order(StringComparer.Ordinal));
    }

    [Fact(DisplayName = "Published Payload Plan Rejects Damaged Candidate Bytes And Its Lease Does Not Expose A Disposed Plan")]
    public void RejectsDamagedCandidates()
    {
        using var fixture = PayloadTestFixture.Create(JournalKind.ExtensionApply, PayloadAction.Install);
        var prepared = fixture.Codec.Freeze(fixture.Plan);
        var reference = prepared.Reference;
        var candidate = prepared.Plan.Candidate.Contents[0];
        prepared.Dispose();
        prepared.Dispose();
        Assert.Throws<ObjectDisposedException>(() => prepared.Plan);
        Assert.Throws<ObjectDisposedException>(() => prepared.Reference);
        File.WriteAllBytes(Path.Combine(fixture.Instance.RuntimeRoot, candidate.RelativePath), [99, 88]);
        Assert.Throws<IOException>(() => fixture.Codec.Read(reference));
        Assert.Equal(FileObjectKind.Missing, fixture.Scanner.Capture(fixture.Plan.TargetPath, default).Shape);
    }

}
