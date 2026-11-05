using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Maintenance;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class UpdateSourceTests
{
    [Theory(DisplayName = "Host And Extension Payload Sources Are Required Only For Their Own Current Operation Kind")]
    [InlineData("missing")]
    [InlineData("extension-source")]
    [InlineData("extension-kind")]
    public void RequiresTypedSources(string fault)
    {
        using var fixture = PayloadTestFixture.Create(JournalKind.UpdateApply, PayloadAction.Upgrade);
        var plan = fault switch
        {
            "missing" => fixture.Plan with { MaintenanceSource = null },
            "extension-source" => fixture.Plan with { ExtensionSource = fixture.Plan.MaintenanceSource },
            "extension-kind" => fixture.Plan with { Kind = JournalKind.ExtensionApply, ExtensionId = "fixture-extension",
                TargetPath = Path.Combine(fixture.Roots.ExtensionPayloadRoot, "fixture-extension") },
            _ => throw new InvalidOperationException()
        };
        Assert.Throws<IOException>(() => fixture.Codec.Freeze(plan));
        Assert.Empty(new RecoveryJournalRegistry(fixture.Database).ReadSnapshot().Journals);
        Assert.Equal(fixture.Plan.Before.Snapshot.Entries, fixture.Scanner.Capture(fixture.Plan.TargetPath, default).Entries);
    }

    [Fact(DisplayName = "A Recorded Feed Key Cannot Be Replaced By Source Metadata Before Payload Acceptance")]
    public void BindsOriginalTrust()
    {
        using var fixture = PayloadTestFixture.Create(JournalKind.UpdateApply, PayloadAction.Upgrade);
        var sourceCodec = new UpdateSourceCodec(fixture.Instance, new());
        var source = sourceCodec.ReadMetadata(fixture.Plan.MaintenanceSource!);
        source = source with { Feed = source.Feed with { KeyId = "caller-chosen" } };
        var changed = fixture.Plan with { MaintenanceSource = sourceCodec.Persist(source) };
        Assert.Throws<IOException>(() => new PayloadApplyAcceptance(fixture.Instance, new(), fixture.Database, fixture.Roots)
            .Accept(new OperationIdentity(changed.OperationId, "fixture", new('a', 64), "fixture.payload"), changed, DateTimeOffset.UtcNow));
        using var connection = DatabaseConnections.Open(fixture.Database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        Assert.Null(OperationRows.ReadOwned(connection, transaction, changed.OperationId, "fixture"));
        transaction.Commit();
        Assert.Empty(new RecoveryJournalRegistry(fixture.Database).ReadSnapshot().Journals);
        Assert.Equal(fixture.Plan.Before.Snapshot.Entries, fixture.Scanner.Capture(fixture.Plan.TargetPath, default).Entries);
    }

    [Fact(DisplayName = "Cold Payload Plan Reads Verify The Original Host Archive Even For No Effect Recovery")]
    public void VerifiesColdArchive()
    {
        using var fixture = PayloadTestFixture.Create(JournalKind.UpdateApply, PayloadAction.Upgrade);
        var source = new UpdateSourceCodec(fixture.Instance, new()).ReadMetadata(fixture.Plan.MaintenanceSource!);
        NexusPipeline.Platform.Storage.BlobReference reference;
        using (var prepared = fixture.Codec.Freeze(fixture.Plan)) reference = prepared.Reference;
        var archive = Path.Combine(fixture.Instance.RuntimeRoot, source.Archive.RelativePath);
        File.WriteAllBytes(archive, [73]);
        Assert.Throws<IOException>(() => fixture.Codec.Read(reference));
        Assert.Equal(fixture.Plan.Before.Snapshot.Entries, fixture.Scanner.Capture(fixture.Plan.TargetPath, default).Entries);
    }

    [Fact(DisplayName = "A Valid No Effect Source Archive Does Not Qualify Its Fixture Bytes For Production Execution")]
    public void RejectsFixtureExecution()
    {
        using var fixture = PayloadTestFixture.Create(JournalKind.UpdateApply, PayloadAction.Upgrade);
        var codec = new UpdateSourceCodec(fixture.Instance, new());
        var source = codec.ReadMetadata(fixture.Plan.MaintenanceSource!);
        var error = Assert.Throws<IOException>(() => codec.RequireTrustedContents(source, fixture.Plan, fixture.Database,
            UpdateSourceSeed.Verifier(new()), default));
        Assert.Equal("update_payload_profile_invalid", error.Message);
        Assert.Empty(new RecoveryJournalRegistry(fixture.Database).ReadSnapshot().Journals);
    }

    [Fact(DisplayName = "The Actual Test Host Assembly Cannot Be Accepted As A Production Host Update")]
    public void RejectsTestHosts()
    {
        var path = Path.ChangeExtension(Environment.GetEnvironmentVariable("NEXUS_TEST_HOST_EXE")!, ".dll");
        using var input = File.OpenRead(path);
        var error = Assert.Throws<IOException>(() => UpdateBuildProfile.RequireProduction(input));
        Assert.Equal("update_payload_profile_invalid", error.Message);
    }
}
