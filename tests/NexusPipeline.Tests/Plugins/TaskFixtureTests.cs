using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit;

namespace NexusPipeline.Tests.Plugins;

public sealed class TaskFixtureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nxp-fixtures-" + Guid.NewGuid().ToString("N"));

    private string Write(string relative, string text = "{}")
    {
        string path = Path.GetFullPath(Path.Combine(_root, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void NestedFixturesKeepStableIdsAndExcludeResourceSets()
    {
        string first = Write("BAAH/baah-daily-flow.json");
        string second = Write("shared/sup-action-next-restart.json");
        Write("shared/resources/source.json");
        var index = TaskFixtures.Index(_root);
        Assert.Equal(2, index.Count);
        Assert.Equal(first, index["baah-daily-flow"]);
        Assert.Equal(second, index["sup-action-next-restart"]);
        Assert.False(index.ContainsKey("source"));
    }

    [Fact]
    public void DuplicateAndCaseCollidingIdsFailClosed()
    {
        Write("A/case.json");
        string duplicate = Write("B/case.json");
        Assert.Throws<InvalidDataException>(() => TaskFixtures.Index(_root));
        File.Delete(duplicate);
        Write("B/CASE.json");
        Assert.Throws<InvalidDataException>(() => TaskFixtures.Index(_root));
    }

    [Fact]
    public void EmptyAndUngroupedRootsFailClosed()
    {
        Directory.CreateDirectory(_root);
        Assert.Throws<InvalidDataException>(() => TaskFixtures.Index(_root));
        Write("flat.json");
        Assert.Throws<InvalidDataException>(() => TaskFixtures.Index(_root));
    }

    [Fact]
    public void ResourceSetsCannotEscapeSharedScope()
    {
        string file = Write("outside.json", "protected");
        foreach (string relative in new[] { "../outside.json", file, "resources/outside.json", "shared/resources/../outside.json" })
        {
            var fixture = new JsonObject { ["resources"] = new JsonArray(), ["resourceSets"] = new JsonArray(relative) };
            Assert.Throws<InvalidDataException>(() => TaskFixtures.Read(fixture, _root));
            Assert.Equal("protected", File.ReadAllText(file));
        }
    }

    [Fact]
    public void LinkedDirectoryIsRejectedAndTargetPreserved()
    {
        Write("cases/current.json");
        string protectedFile = Write("target/protected.json", "protected");
        string link = Path.Combine(_root, "cases", "link");
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Arguments = $"/d /c mklink /J \"{link}\" \"{Path.GetDirectoryName(protectedFile)}\"";
        using var process = Process.Start(start)!;
        Assert.True(process.WaitForExit(5000));
        Assert.True(process.ExitCode == 0, process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd());
        try
        {
            Assert.Throws<InvalidDataException>(() => TaskFixtures.Index(_root));
            Assert.Equal("protected", File.ReadAllText(protectedFile));
        }
        finally { Directory.Delete(link); }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
