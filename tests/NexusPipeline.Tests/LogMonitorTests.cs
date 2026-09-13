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

    [Fact]
    public void NonZeroTruncateThenAppendReadsNewContentEvenWhenLengthGrows()
    {
        string root = MakeTempDir();
        string path = Path.Combine(root, "run.log");
        try
        {
            File.WriteAllText(path, "old-prefix\n", Encoding.UTF8);
            using var monitor = new LogMonitor(path, readFromStart: false, initialPosition: new FileInfo(path).Length);
            File.AppendAllText(path, "already-read\n", Encoding.UTF8);
            Assert.Equal("already-read\n", monitor.ReadNew());

            byte[] marker = Encoding.UTF8.GetBytes("new-marker\n");
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            {
                stream.SetLength(new FileInfo(path).Length - 3);
                stream.Seek(0, SeekOrigin.End);
                stream.Write(marker);
                stream.Flush(flushToDisk: true);
            }

            Assert.Equal("new-marker\n", monitor.ReadNew());
            Assert.Equal("", monitor.ReadNew());
        }
        finally
        {
            DeleteExact(root);
        }
    }

    [Fact]
    public void SameLengthTruncateAndRegrowReadsChangedSuffix()
    {
        string root = MakeTempDir();
        string path = Path.Combine(root, "run.log");
        try
        {
            File.WriteAllText(path, "old-prefix\n", Encoding.UTF8);
            using var monitor = new LogMonitor(path, readFromStart: false, initialPosition: new FileInfo(path).Length);
            File.AppendAllText(path, "already-read-content\n", Encoding.UTF8);
            Assert.Equal("already-read-content\n", monitor.ReadNew());

            byte[] replacement = Encoding.UTF8.GetBytes("new-suffix-content\n");
            long oldLength = new FileInfo(path).Length;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            {
                stream.SetLength(oldLength - replacement.Length);
                stream.Seek(0, SeekOrigin.End);
                stream.Write(replacement);
                stream.Flush(flushToDisk: true);
            }

            Assert.Equal("new-suffix-content\n", monitor.ReadNew());
            Assert.Equal("", monitor.ReadNew());
        }
        finally
        {
            DeleteExact(root);
        }
    }

    [Fact]
    public void LargeFileKeepsOnlyBoundedCheckpointAndReadsAppendWithoutFullSnapshot()
    {
        string root = MakeTempDir();
        string path = Path.Combine(root, "large.log");
        try
        {
            const int size = 100 * 1024 * 1024;
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            {
                stream.SetLength(size);
            }

            using var monitor = new LogMonitor(path, readFromStart: false, initialPosition: size);

            Assert.Equal(LogMonitor.CheckpointCapacityBytes, monitor.CheckpointBytes);
            File.AppendAllText(path, "new\n", Encoding.UTF8);

            Assert.Equal("new\n", monitor.ReadNew());
            Assert.Equal(LogMonitor.CheckpointCapacityBytes, monitor.CheckpointBytes);
        }
        finally
        {
            DeleteExact(root);
        }
    }

    [Fact]
    public void DeepRewriteOutsideCheckpointWindowFailsConservatively()
    {
        string root = MakeTempDir();
        string path = Path.Combine(root, "deep.log");
        try
        {
            const int size = 8 * 1024 * 1024;
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            {
                stream.SetLength(size);
                stream.Seek(0, SeekOrigin.Begin);
                stream.WriteByte((byte)'a');
                stream.Seek(size - 1, SeekOrigin.Begin);
                stream.WriteByte((byte)'z');
                stream.Flush(flushToDisk: true);
            }

            using var monitor = new LogMonitor(path, readFromStart: true);
            Assert.Equal(size, Encoding.UTF8.GetByteCount(monitor.ReadNew()));

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            {
                stream.Seek(0, SeekOrigin.Begin);
                stream.WriteByte((byte)'b');
                stream.Flush(flushToDisk: true);
            }

            Assert.Equal("", monitor.ReadNew());
            File.AppendAllText(path, "safe-new\n", Encoding.UTF8);
            Assert.Equal("safe-new\n", monitor.ReadNew());
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
