using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class WalEvidenceTests
{
    [Theory(DisplayName = "Actual Host Startup Rejects Unverified Wal And Rollback Groups Before Serving And Preserves The Runtime")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreservesUnverifiedGroups(bool rollback)
    {
        var fixture = CreateStoppedWal();
        if (rollback) File.WriteAllText(fixture.Database.DatabasePath + "-journal", "unknown rollback evidence");
        else
        {
            var path = fixture.Database.DatabasePath + "-wal";
            var bytes = File.ReadAllBytes(path);
            bytes[76] ^= 1;
            File.WriteAllBytes(path, bytes);
        }
        var before = Fingerprints(fixture.Instance.RuntimeRoot);
        using var job = new OwnedWorkerJob();
        var identity = job.Start(Environment.GetEnvironmentVariable("NEXUS_TEST_HOST_EXE")!,
            ["serve", "--runtime-root", fixture.Instance.RuntimeRoot, "--port", "0"], fixture.Root, fixture.Instance.InstanceId, Guid.NewGuid(),
            observed => File.WriteAllBytes(Path.Combine(fixture.Root, "actual-host.launch.json"), System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(observed)));
        using var process = System.Diagnostics.Process.GetProcessById(identity.ProcessId);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(timeout.Token);
        Assert.Equal(1, process.ExitCode);
        for (var count = 0; count < 250 && job.GetAccounting().ActiveProcesses != 0; count++) await Task.Delay(20);
        Assert.Equal(0u, job.GetAccounting().ActiveProcesses);
        Assert.Empty(job.ActiveProcessIds());
        Assert.Equal(before, Fingerprints(fixture.Instance.RuntimeRoot));
    }

    [Fact(DisplayName = "A Complete Stopped Wal Group Is Inspected Only In Its Copy And Keeps Its Committed Operation")]
    public void InspectsCompleteGroups()
    {
        var fixture = CreateStoppedWal();
        var before = Fingerprints(fixture.Instance.RuntimeRoot);
        var inspected = DatabaseInspector.Inspect(fixture.Database.DatabasePath, fixture.Instance.InstanceId, Path.Combine(fixture.Root, "diagnostics"));
        Assert.Equal(before, Fingerprints(fixture.Instance.RuntimeRoot));
        using var connection = new SqliteConnection($"Data Source={Path.Combine(inspected.DiagnosticCopyRoot, "nexus.db")};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT count(*) FROM operations;";
        Assert.Equal(2L, query.ExecuteScalar());
    }

    [Theory(DisplayName = "A Real Checkpoint And Wal Reuse Preserves Committed State Without Changing The Source Group")]
    [InlineData(false)]
    [InlineData(true)]
    public void PreservesCheckpointState(bool multipleGenerations)
    {
        var fixture = CreateStoppedWal(reuse: true, multipleGenerations: multipleGenerations);
        var before = Fingerprints(fixture.Instance.RuntimeRoot);
        var wal = File.ReadAllBytes(fixture.Database.DatabasePath + "-wal");
        var pageSize = checked((int)BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(8)));
        var salts = wal.AsSpan(16, 8).ToArray();
        var stale = 0;
        for (var offset = 32; offset < wal.Length; offset += pageSize + 24)
            if (!wal.AsSpan(offset + 8, 8).SequenceEqual(salts)) stale++;
        Assert.True(stale > 0, "SQLite did not retain an old WAL generation in this fixture");
        var inspected = DatabaseInspector.Inspect(fixture.Database.DatabasePath, fixture.Instance.InstanceId, Path.Combine(fixture.Root, "diagnostics"));
        Assert.Equal(stale, inspected.Wal!.InvalidatedTailFrames);
        Assert.Equal(multipleGenerations ? 2 : 1, inspected.Wal.InvalidatedTailGenerations);
        Assert.True(inspected.Wal.CurrentGenerationCommits > 0);
        Assert.Equal(before, Fingerprints(fixture.Instance.RuntimeRoot));
        using var connection = new SqliteConnection($"Data Source={Path.Combine(inspected.DiagnosticCopyRoot, "nexus.db")};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT count(*) FROM operations;";
        Assert.Equal(multipleGenerations ? 33L : 23L, query.ExecuteScalar());
    }

    [Theory(DisplayName = "Wal Reuse Cannot Hide Current Frame Corruption Or An Unrecognized Tail")]
    [InlineData("current-salt-disguised-as-old")]
    [InlineData("tail-chain")]
    [InlineData("tail-truncated")]
    [InlineData("tail-future-generation")]
    [InlineData("tail-same-generation-random-salt")]
    [InlineData("current-generation-after-tail")]
    public void RejectsCorruptFrames(string fault)
    {
        var fixture = CreateStoppedWal(reuse: true);
        var path = fixture.Database.DatabasePath + "-wal";
        var bytes = File.ReadAllBytes(path);
        var pageSize = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8)));
        var stride = pageSize + 24;
        var tail = 32;
        while (bytes.AsSpan(tail + 8, 8).SequenceEqual(bytes.AsSpan(16, 8))) tail += stride;
        Assert.True(tail + stride < bytes.Length);
        switch (fault)
        {
            case "current-salt-disguised-as-old": bytes.AsSpan(tail + 8, 8).CopyTo(bytes.AsSpan(40, 8)); break;
            case "tail-chain": bytes[tail + stride + 76] ^= 1; break;
            case "tail-truncated": bytes = bytes[..^1]; break;
            case "tail-future-generation":
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(tail + 8), unchecked(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16)) + 1)); break;
            case "tail-same-generation-random-salt": bytes[tail + stride + 12] ^= 1; break;
            case "current-generation-after-tail": bytes.AsSpan(32, stride).CopyTo(bytes.AsSpan(bytes.Length - stride)); break;
        }
        File.WriteAllBytes(path, bytes);
        var before = Fingerprints(fixture.Instance.RuntimeRoot);
        Assert.Throws<IOException>(() => DatabaseInspector.Inspect(fixture.Database.DatabasePath, fixture.Instance.InstanceId, Path.Combine(fixture.Root, "diagnostics")));
        Assert.Equal(before, Fingerprints(fixture.Instance.RuntimeRoot));
    }

    [Theory(DisplayName = "Unverified Wal Or Rollback Evidence Cannot Be Silently Ignored Or Changed")]
    [InlineData("torn-header")]
    [InlineData("torn-frame")]
    [InlineData("unknown-version")]
    [InlineData("invalid-page-size")]
    [InlineData("header-checksum")]
    [InlineData("frame-checksum")]
    [InlineData("frame-generation")]
    [InlineData("rollback-journal")]
    public void PreservesUnknownEvidence(string fault)
    {
        var fixture = CreateStoppedWal();
        var path = fixture.Database.DatabasePath + "-wal";
        var bytes = File.ReadAllBytes(path);
        switch (fault)
        {
            case "torn-header": bytes = bytes[..17]; break;
            case "torn-frame": bytes = bytes[..^1]; break;
            case "unknown-version": bytes[4] ^= 0x10; break;
            case "invalid-page-size": BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 32767); break;
            case "header-checksum": bytes[24] ^= 1; break;
            case "frame-checksum": bytes[56 + 20] ^= 1; break;
            case "frame-generation": bytes[40] ^= 1; break;
            case "rollback-journal": File.WriteAllText(fixture.Database.DatabasePath + "-journal", "preserve unknown rollback state"); break;
        }
        File.WriteAllBytes(path, bytes);
        var before = Fingerprints(fixture.Instance.RuntimeRoot);
        Assert.Throws<IOException>(() => StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Root, "diagnostics")));
        Assert.Equal(before, Fingerprints(fixture.Instance.RuntimeRoot));
    }

    internal static (string Root, RuntimeInstance Instance, StateDatabase Database) CreateStoppedWal(bool reuse = false, bool multipleGenerations = false)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, Guid.NewGuid().ToString("N"));
        var instance = new InstanceRoot(new ContractReader()).OpenOrCreate(Path.Combine(root, "runtime"));
        using var instanceLease = InstanceLease.Acquire(instance, new ContractReader());
        var database = StateDatabase.CreateFresh(instance);
        Dictionary<string, byte[]> captured;
        using (var anchor = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = PhysicalPathPolicy.NativePath(database.DatabasePath), Pooling = false }.ToString()))
        {
            anchor.Open();
            using var setting = anchor.CreateCommand();
            setting.CommandText = "PRAGMA wal_autocheckpoint=0;";
            setting.ExecuteNonQuery();
            setting.CommandText = "SELECT count(*) FROM operations;";
            setting.ExecuteScalar();
            database.Accept(new(Guid.NewGuid(), "fixture", new string('a', 64), "fixture.command"), DateTimeOffset.UtcNow);
            database.Accept(new(Guid.NewGuid(), "fixture", new string('b', 64), "fixture.command"), DateTimeOffset.UtcNow);
            if (reuse)
            {
                for (var index = 0; index < 20; index++)
                    database.Accept(new(Guid.NewGuid(), "fixture", new string('c', 64), "fixture.command"), DateTimeOffset.UtcNow);
                var checkpoint = database.Checkpoint(CheckpointMode.Passive);
                Assert.Equal(checkpoint.LogFrames, checkpoint.CheckpointedFrames);
                if (multipleGenerations)
                {
                    for (var index = 0; index < 10; index++)
                        database.Accept(new(Guid.NewGuid(), "fixture", new string('e', 64), "fixture.command"), DateTimeOffset.UtcNow);
                    var secondCheckpoint = database.Checkpoint(CheckpointMode.Passive);
                    Assert.Equal(secondCheckpoint.LogFrames, secondCheckpoint.CheckpointedFrames);
                }
                database.Accept(new(Guid.NewGuid(), "fixture", new string('d', 64), "fixture.command"), DateTimeOffset.UtcNow);
            }
            captured = new[] { "", "-wal", "-shm" }.ToDictionary(suffix => suffix, suffix => ReadOwnedQuiescent(database.DatabasePath + suffix));
        }
        foreach (var (suffix, bytes) in captured) File.WriteAllBytes(database.DatabasePath + suffix, bytes);
        return (root, instance, database);
    }

    private static byte[] ReadOwnedQuiescent(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        return bytes;
    }

    private static string[] Fingerprints(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
}
