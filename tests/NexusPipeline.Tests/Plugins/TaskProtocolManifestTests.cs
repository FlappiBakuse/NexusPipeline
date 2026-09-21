using System.Text.Json.Nodes;
using NexusPipeline.Modules.Plugins;
using Xunit;

namespace NexusPipeline.Tests.Plugins;

public sealed class TaskProtocolManifestTests
{
    private static JsonObject Manifest() => (JsonObject)JsonNode.Parse("""
        {"kind":"data-specialized","judgeScript":"data/judge.js","minHostVersion":"0.16.8",
         "taskProtocol":{"version":"1.0","discoverScript":"data/discover.js","retryScript":"data/retry.js","readResources":[]}}
        """)!;

    [Theory]
    [InlineData("version", "2.0")]
    [InlineData("discoverScript", "../secret.js")]
    [InlineData("retryScript", "data/retry.py")]
    [InlineData("retryScript", "data/C:retry.js")]
    [InlineData("retryScript", "data/../retry.js")]
    public void RejectsInvalidProtocol(string field, string value)
    {
        var manifest = Manifest(); manifest["taskProtocol"]![field] = value;
        Assert.False(TaskProtocolManifest.TryValidate(manifest, out var error));
        Assert.Contains("taskProtocol", error);
    }

    [Theory]
    [InlineData("0.16.7")]
    [InlineData("0.16.8-rc.1")]
    public void RequiresSupportingHost(string version)
    {
        var manifest = Manifest(); manifest["minHostVersion"] = version;
        Assert.False(TaskProtocolManifest.TryValidate(manifest, out _));
    }

    [Fact]
    public void NullOrManagedDeclarationCannotFallback()
    {
        var manifest = Manifest(); manifest["kind"] = "managed-code";
        Assert.False(TaskProtocolManifest.TryValidate(manifest, out _));
        manifest = Manifest(); manifest["taskProtocol"] = null;
        Assert.False(TaskProtocolManifest.TryValidate(manifest, out _));
        manifest.Remove("taskProtocol");
        Assert.True(TaskProtocolManifest.TryValidate(manifest, out _));
    }

    [Fact]
    public void FrozenSourceDoesNotFollowPackageChanges()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-task-assets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "data"));
        try
        {
            foreach (string file in new[] { "discover", "judge", "retry" }) File.WriteAllText(Path.Combine(root, "data", file + ".js"), "// original");
            var descriptor = TaskProtocolManifest.Freeze(Manifest(), root)!;
            File.WriteAllText(Path.Combine(root, "data", "judge.js"), "// changed");
            Assert.Equal("// original", descriptor.ObserveScript);
            File.Delete(Path.Combine(root, "data", "retry.js"));
            Assert.Throws<FileNotFoundException>(() => TaskProtocolManifest.Freeze(Manifest(), root));
        }
        finally { Directory.Delete(root, true); }
    }
}
