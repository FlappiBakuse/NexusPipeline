using NexusPipeline.Models;
using NexusPipeline.Services;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class ConfigSwapPrimitivesTests
{
    [Fact]
    public void CopyAs_FileAndDirectoryKeepContents()
    {
        string root = MakeTempDir();
        try
        {
            string sourceFile = Path.Combine(root, "source.json");
            string fileTarget = Path.Combine(root, "nested", "target.json");
            File.WriteAllText(sourceFile, "{\"value\":1}");

            ConfigSwapPrimitives.CopyAs(sourceFile, fileTarget, PathKind.File);

            string sourceDir = Path.Combine(root, "source-dir");
            string dirTarget = Path.Combine(root, "dir-target");
            Directory.CreateDirectory(Path.Combine(sourceDir, "child"));
            File.WriteAllText(Path.Combine(sourceDir, "child", "state.txt"), "state");
            ConfigSwapPrimitives.CopyAs(sourceDir, dirTarget, PathKind.Dir);

            Assert.Equal("{\"value\":1}", File.ReadAllText(fileTarget));
            Assert.Equal("state", File.ReadAllText(Path.Combine(dirTarget, "child", "state.txt")));
        }
        finally
        {
            DeleteExact(root);
        }
    }

    [Fact]
    public void MoveAs_RemovesSourceAfterCopy()
    {
        string root = MakeTempDir();
        try
        {
            string source = Path.Combine(root, "source");
            string target = Path.Combine(root, "target");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "state.txt"), "state");

            ConfigSwapPrimitives.MoveAs(source, target, PathKind.Dir);

            Assert.False(Directory.Exists(source));
            Assert.Equal("state", File.ReadAllText(Path.Combine(target, "state.txt")));
        }
        finally
        {
            DeleteExact(root);
        }
    }

    private static string MakeTempDir()
    {
        string root = Path.Combine(Path.GetTempPath(), "np-config-swap-" + Guid.NewGuid().ToString("N"));
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
