using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using NexusPipeline.Modules.Updates;
using NexusPipeline.Shared.Versioning;
using Xunit;

namespace NexusPipeline.Tests.Updates;

public sealed class UpdateRunnerTests
{
    [Fact]
    public void SpacePlanIncludesFallbackBackupReconstructionWorkerAndRecoveryReserve()
    {
        var plan = UpdateStoragePlan.Create(20, 100, 1000, 900, 50, 300);
        Assert.True(plan.PeakAdditionalBytes > 20 + 2 * 1000 + 900 + 50 + 300);
        plan.RequireAvailable(plan.PeakAdditionalBytes);
        Assert.Throws<IOException>(() => plan.RequireAvailable(plan.PeakAdditionalBytes - 1));
        Assert.Throws<InvalidDataException>(() => UpdateStoragePlan.Create(-1, 1, 1, 1, 1));
        Assert.Throws<OverflowException>(() => UpdateStoragePlan.Create(1, 1, long.MaxValue, 1, 1));
        var primary = new NexusPipeline.Platform.Storage.VolumeSpace("D:\\", plan.PeakAdditionalBytes + 1024L * 1024 * 1024, 4096);
        var manager = new NexusPipeline.Platform.Storage.VolumeSpace("C:\\", primary.AvailableBytes, 65536);
        plan.RequireAvailable([primary, manager]);
        Assert.Throws<IOException>(() => plan.RequireAvailable([primary, manager with { AvailableBytes = plan.PeakAdditionalBytes }]));
        Assert.Throws<IOException>(() => plan.RequireAvailable([primary with { AvailableBytes = plan.PeakAdditionalBytes }, manager]));
        Assert.Throws<InvalidDataException>(() => plan.RequireAvailable([primary with { AllocationUnitBytes = 0 }]));
        var actual = NexusPipeline.Platform.Storage.VolumeSpaceProbe.Read(Path.GetTempPath());
        Assert.True(actual.AllocationUnitBytes > 0); Assert.True(actual.AvailableBytes >= 0);
    }
    private static UpdateRunnerFrame Hello => new("hello", Guid.NewGuid().ToString("N"), new('a', 64), 1, 0,
        new('b', 64), UpdateTargetManifest.Capabilities, [], null, null, null);

    [Fact]
    public async Task HandshakeRequiresExactNonceBuildProtocolCapabilitiesAndMonotonicSequence()
    {
        var expected = Hello;
        await using var stream = new MemoryStream();
        await UpdateMigrationRunner.WriteFrameAsync(stream, expected, default);
        stream.Position = 0;
        var actual = await UpdateMigrationRunner.ReadFrameAsync(stream, default);
        UpdateMigrationRunner.ValidateFrame(actual, expected, "hello", 0);
        foreach (var invalid in new[] { actual with { Nonce = new('c', 64) }, actual with { TransactionId = Guid.NewGuid().ToString("N") },
            actual with { BuildId = new('d', 64) }, actual with { Sequence = 2 }, actual with { Protocol = 2 },
            actual with { Capabilities = ["payload-manifest-v1"] }, actual with { Capabilities = [.. actual.Capabilities, actual.Capabilities[0]] },
            actual with { MigratorIds = ["unregistered"] }, actual with { Kind = "result" } })
            Assert.Throws<InvalidDataException>(() => UpdateMigrationRunner.ValidateFrame(invalid, expected, "hello", 0));
        Assert.Throws<InvalidDataException>(() => UpdateMigrationRegistry.Validate("unknown-route", new([]), new([])));
        Assert.Throws<InvalidDataException>(() => UpdateMigrationRegistry.Validate(UpdateMigrationRegistry.NoStoreChangeRoute,
            new([new("user.json", false, 1, new('a', 64))]), new([])));
    }

    [Fact]
    public async Task FramesRejectOversizeUnknownMissingDuplicateAndTruncatedFields()
    {
        string valid = JsonSerializer.Serialize(Hello);
        foreach (string json in new[] { valid.Replace("\"Protocol\":1", "\"Protocol\":1,\"Protocol\":1", StringComparison.Ordinal),
            valid.Replace("\"Protocol\":1", "\"Protocol\":1,\"Command\":\"ignored\"", StringComparison.Ordinal),
            valid.Replace("\"Protocol\":1,", "", StringComparison.Ordinal) })
        {
            byte[] body = Encoding.UTF8.GetBytes(json), header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
            await using var stream = new MemoryStream([.. header, .. body]);
            await Assert.ThrowsAsync<InvalidDataException>(() => UpdateMigrationRunner.ReadFrameAsync(stream, default));
        }
        byte[] large = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(large, 65537);
        await using var oversized = new MemoryStream(large);
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateMigrationRunner.ReadFrameAsync(oversized, default));
        await using var truncated = new MemoryStream([1, 0, 0, 0]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => UpdateMigrationRunner.ReadFrameAsync(truncated, default));
    }

    [Fact]
    public void ReleaseEvidenceSurvivesJournalRoundTripAndCannotBeChangedDuringRecovery()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-runner-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string version = "0.17.3";
            string prefix = "NexusPipeline-v0.17.3-win-x64";
            var payload = new UpdatePayloadIdentity(new('a', 64), new('b', 64), new('c', 64), "g0170");
            string[] names = [prefix + ".zip", prefix + "-setup.exe", prefix + "-update.json",
                .. UpdateTargetManifest.ComponentIds.Select(id => prefix + "-" + id + ".zip")];
            var assets = names.Select((name, index) => new UpdateReleaseAsset(index + 1, name, "https://github.com/" + name, 1, new('d', 64))).ToDictionary(asset => asset.Name);
            Assert.True(NexusVersion.TryParse(version, out var parsed));
            var release = new ReleaseInfo(parsed, "v" + version, "", "", true, 12, assets[names[0]], assets[names[2]], assets);
            var evidence = UpdateReleaseEvidence.Freeze(release, payload)!;
            string file = Path.Combine(root, "task.json");
            var task = UpdateTask.Create("defer", version, Path.Combine(root, "staging")) with { TargetPayload = payload,
                PackageSha256 = new('e', 64), StagingInventory = new([]), ReleaseEvidence = evidence, JournalSchemaVersion = 2 };
            task.Write(file);
            Assert.Equal(evidence, UpdateTask.Read(file)!.ReleaseEvidence);
            Assert.Throws<InvalidDataException>(() => (task with { ReleaseEvidence = evidence with { ReleaseId = 13 } }).Write(file));
            Assert.Throws<InvalidDataException>(() => (task with { ReleaseEvidence = evidence with { Assets = assets.Values.Select(asset => asset with { Id = 1 }).ToArray() } }).Write(file));
            Assert.Throws<InvalidDataException>(() => (task with { JournalSchemaVersion = 1 }).Write(file));
            Assert.Throws<InvalidDataException>(() => (task with { RunnerReceipt = new(new('a', 64), new('b', 64), null, false, 2, true, new([]), new([])) }).Write(file));
            Assert.Equal(evidence, UpdateTask.Read(file)!.ReleaseEvidence);
        }
        finally { Directory.Delete(root, true); }
    }
}
