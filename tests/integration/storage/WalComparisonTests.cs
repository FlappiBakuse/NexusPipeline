using Microsoft.Data.Sqlite;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class WalComparisonTests(ITestOutputHelper output)
{
    [Theory(DisplayName = "Existing Wal Rules And The Actual Sqlite Engine Are Compared Only On Stopped Owned Copies")]
    [InlineData("normal")]
    [InlineData("incomplete-tail")]
    [InlineData("committed-corruption")]
    [InlineData("unknown-format")]
    public void ComparesStoppedCopies(string scenario)
    {
        var fixture = WalEvidenceTests.CreateStoppedWal();
        var path = fixture.Database.DatabasePath;
        if (scenario == "incomplete-tail")
        {
            var bytes = File.ReadAllBytes(path + "-wal");
            using var tail = new FileStream(path + "-wal", FileMode.Append, FileAccess.Write, FileShare.None);
            tail.Write(bytes.AsSpan(32, 17)); tail.Flush(true);
        }
        if (scenario == "committed-corruption")
        {
            var bytes = File.ReadAllBytes(path + "-wal"); bytes[32 + 24 + 76] ^= 1;
            File.WriteAllBytes(path + "-wal", bytes);
        }
        if (scenario == "unknown-format")
        {
            var bytes = File.ReadAllBytes(path); bytes[0] ^= 0x40; File.WriteAllBytes(path, bytes);
        }
        var before = Fingerprints();
        var productError = Record.Exception(() => DatabaseInspector.Inspect(path, fixture.Instance.InstanceId,
            Path.Combine(fixture.Root, "product-diagnostic")));
        Assert.Equal(before, Fingerprints());
        var copy = Path.Combine(fixture.Root, "sqlite-copy"); Directory.CreateDirectory(copy);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(path + suffix)) File.Copy(path + suffix, Path.Combine(copy, "nexus.db" + suffix));
        string? version = null, integrity = null; long? operations = null; int? sqliteCode = null;
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = PhysicalPathPolicy.NativePath(Path.Combine(copy, "nexus.db")), Pooling = false,
                Mode = SqliteOpenMode.ReadWrite }.ToString());
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT sqlite_version();"; version = (string)command.ExecuteScalar()!;
            command.CommandText = "PRAGMA integrity_check;"; integrity = (string)command.ExecuteScalar()!;
            command.CommandText = "SELECT count(*) FROM operations;"; operations = (long)command.ExecuteScalar()!;
        }
        catch (SqliteException error) { sqliteCode = error.SqliteErrorCode; }
        Assert.Equal(before, Fingerprints());
        if (scenario == "normal")
        { Assert.Null(productError); Assert.Null(sqliteCode); Assert.Equal("ok", integrity); Assert.Equal(2L, operations); }
        else
        {
            Assert.IsType<IOException>(productError);
            if (scenario == "incomplete-tail")
            { Assert.Null(sqliteCode); Assert.Equal("ok", integrity); Assert.Equal(2L, operations); }
            if (scenario == "committed-corruption")
                Assert.True(sqliteCode is not null || operations is < 2 || integrity != "ok");
            if (scenario == "unknown-format") Assert.Equal(26, sqliteCode);
        }
        output.WriteLine(JsonSerializer.Serialize(new { comparison = "wal-bounded-stopped-copy-v1", scenario,
            sqliteVersion = version, integrity, operations, sqliteCode,
            productAccepted = productError is null, productRejection = productError?.Message,
            originalPreserved = true, sourceGroup = before,
            scope = "Owned quiescent complete group restored after its anchor connection closed; SQLite may mutate only its independent copy. Synthetic partial/corrupt bytes do not represent a real power-loss experiment." }));

        string[] Fingerprints() => new[] { "", "-wal", "-shm" }.Where(suffix => File.Exists(path + suffix))
            .Select(suffix => suffix + ":" + new FileInfo(path + suffix).Length + ":" +
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path + suffix)))).ToArray();
    }
}
