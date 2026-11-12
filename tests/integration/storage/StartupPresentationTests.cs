using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class StartupPresentationTests
{
    [Fact(DisplayName = "Fresh Startup Presentation Does Not Initialize A Database")]
    public void KeepsFreshState()
    {
        var instance = Create();
        using var lease = InstanceLease.Acquire(instance, new());
        var before = Files(instance);
        var value = StartupPresentation.Read(instance, lease);
        Assert.Equal(HostSettings.Default.Desktop, value.Desktop);
        Assert.Equal(before, Files(instance));
    }

    [Theory(DisplayName = "Startup Presentation Reads Saved Desktop Preferences Without Changing Database Bytes")]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadsSavedPresentation(bool browser)
    {
        var instance = Create();
        var database = StateDatabase.CreateFresh(instance);
        using var lease = InstanceLease.Acquire(instance, new());
        var settings = HostSettings.Default with { Desktop = new(browser ? DesktopMode.Browser : DesktopMode.Desktop, false, true), Locale = HostLocale.EnUs };
        var change = new HostSettingsChange(0, settings);
        var store = new HostSettingsStore(database);
        store.Commit(HostSettingsStore.Identity(Guid.NewGuid(), "fixture", change), change, DateTimeOffset.UtcNow);
        var before = Files(instance);
        var value = StartupPresentation.Read(instance, lease);
        Assert.Equal(settings.Desktop, value.Desktop);
        Assert.Equal(settings.Locale, value.Locale);
        Assert.Equal(before, Files(instance));
    }

    [Fact(DisplayName = "Unknown Startup Database Identity Is Preserved And Refused Before Presentation")]
    public void PreservesUnknownPresentation()
    {
        var instance = Create();
        var database = StateDatabase.CreateFresh(instance);
        using var lease = InstanceLease.Acquire(instance, new());
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database.DatabasePath, Pooling = false }.ToString()))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=91;"; command.ExecuteNonQuery();
        }
        var before = Files(instance);
        Assert.Throws<IOException>(() => StartupPresentation.Read(instance, lease));
        Assert.Equal(before, Files(instance));
    }

    [Fact(DisplayName = "Presentation Queries Reject Sql Writes And Leave All Original Files Unchanged")]
    public void RejectsPresentationWrites()
    {
        var instance = Create(); var database = StateDatabase.CreateFresh(instance);
        using var lease = InstanceLease.Acquire(instance, new());
        var before = Files(instance);
        using (var reading = new StartupDatabaseRead(database.DatabasePath))
        {
            using var command = reading.Connection.CreateCommand(); command.CommandText = "DELETE FROM settings_current;";
            Assert.Equal(8, Assert.Throws<SqliteException>(() => command.ExecuteNonQuery()).SqliteErrorCode);
        }
        Assert.Equal(before, Files(instance));
    }

    private static RuntimeInstance Create() => new InstanceRoot(new()).OpenOrCreate(Path.Combine(
        Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "presentation-" + Guid.NewGuid().ToString("N")));

    [Fact(DisplayName = "Startup Presentation Includes Committed Wal Settings Without Touching Original Wal")]
    public void ReadsCommittedWal()
    {
        var instance = Create();
        var database = StateDatabase.CreateFresh(instance);
        byte[] original, wal;
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database.DatabasePath, Pooling = false }.ToString()))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_autocheckpoint=0; UPDATE settings_snapshots SET desktop_mode=1 WHERE revision=0;";
            command.ExecuteNonQuery();
            original = ReadShared(database.DatabasePath);
            wal = ReadShared(database.DatabasePath + "-wal");
        }
        File.WriteAllBytes(database.DatabasePath, original);
        File.WriteAllBytes(database.DatabasePath + "-wal", wal);
        using var lease = InstanceLease.Acquire(instance, new());
        var before = Files(instance);
        Assert.Equal(DesktopMode.Browser, StartupPresentation.Read(instance, lease).Desktop.Mode);
        Assert.Equal(before, Files(instance));
    }

    private static byte[] ReadShared(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var bytes = new MemoryStream(); input.CopyTo(bytes); return bytes.ToArray();
    }

    private static string[] Files(RuntimeInstance instance) => Directory.EnumerateFiles(instance.RuntimeRoot, "*", SearchOption.AllDirectories)
        .Where(path => !path.EndsWith("instance.lock", StringComparison.Ordinal)).Order(StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(instance.RuntimeRoot, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
}
