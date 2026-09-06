using System.Reflection;
using System.Text;
using NexusPipeline.Services;
using NexusPipeline.Services.Execution;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class LogMonitorTests
{
    [Fact]
    public void ReadsOnlyNewContentAfterInitialPosition()
    {
        string root = MakeTempDir();
        string path = Path.Combine(root, "run.log");
        try
        {
            File.WriteAllText(path, "old\n");
            using var monitor = new LogMonitor(path, readFromStart: false, initialPosition: new FileInfo(path).Length);

            File.AppendAllText(path, "new\n");

            Assert.Equal("new\n", monitor.ReadNew());
            Assert.Equal("", monitor.ReadNew());
        }
        finally
        {
            DeleteExact(root);
        }
    }

    [Fact]
    public void TransientReopenResumesCommittedOffset()
    {
        string root = MakeTempDir();
        string path = Path.Combine(root, "run.log");
        try
        {
            File.WriteAllText(path, "old\n", Encoding.UTF8);
            using var monitor = new LogMonitor(path, readFromStart: true);
            Assert.Equal("old\n", monitor.ReadNew());

            FieldInfo streamField = typeof(LogMonitor).GetField("_stream", BindingFlags.Instance | BindingFlags.NonPublic)!;
            ((FileStream)streamField.GetValue(monitor)!).Dispose();
            File.AppendAllText(path, "new\n", Encoding.UTF8);

            Assert.Equal("", monitor.ReadNew());
            Assert.Equal("new\n", monitor.ReadNew());
        }
        finally
        {
            DeleteExact(root);
        }
    }

    [Fact]
    public void FileIdentityFallbackDetectsReplacementWhenNativeIdIsUnavailable()
    {
        string root = MakeTempDir();
        string path = Path.Combine(root, "run.log");
        try
        {
            File.WriteAllText(path, "old", Encoding.UTF8);
            using var monitor = new LogMonitor(path, readFromStart: true);
            FieldInfo validField = typeof(LogMonitor).GetField("_fileIdValid", BindingFlags.Instance | BindingFlags.NonPublic)!;
            validField.SetValue(monitor, false);
            long oldStamp = monitor.FileStamp;
            File.Move(path, path + ".old");
            File.WriteAllText(path, "new", Encoding.UTF8);
            File.SetCreationTimeUtc(path, new DateTime(oldStamp, DateTimeKind.Utc).AddSeconds(1));

            Assert.True(monitor.FileReplaced(path));
        }
        finally
        {
            DeleteExact(root);
        }
    }

    private static string MakeTempDir()
    {
        string root = Path.Combine(Path.GetTempPath(), "np-log-monitor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteExact(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
