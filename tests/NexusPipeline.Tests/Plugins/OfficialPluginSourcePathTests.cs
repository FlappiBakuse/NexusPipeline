using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Xunit;
using NexusPipeline.Modules.Plugins.Repository;

namespace NexusPipeline.Tests.Plugins;

public sealed class OfficialPluginSourcePathTests
{
    [Theory]
    [InlineData("managed-code", "CustomWallpaper", "plugins/general/CustomWallpaper/README.md")]
    [InlineData("data-specialized", "BetterGI", "plugins/specialized/BetterGI/README.md")]
    public void ReadmeUri_UsesKindSpecificOfficialSourceDirectory(
        string kind,
        string artifactName,
        string expectedPath)
    {
        Assert.True(OfficialPluginSourcePaths.TryGetReadmeUri(
            kind,
            artifactName,
            out Uri? uri,
            out string? error), error);
        Assert.Equal(
            "https://raw.githubusercontent.com/FlappiBakuse/NexusPipeline-Plugins/main/" + expectedPath,
            uri!.AbsoluteUri);
    }

    [Theory]
    [InlineData("unknown", "CustomWallpaper")]
    [InlineData("managed-code", "../CustomWallpaper")]
    [InlineData("data-specialized", "CustomWallpaper/")]
    public void ReadmeUri_RejectsUnknownKindOrUnsafeArtifact(
        string kind,
        string artifactName)
    {
        Assert.False(OfficialPluginSourcePaths.TryGetReadmeUri(
            kind,
            artifactName,
            out Uri? uri,
            out string? error));
        Assert.Null(uri);
        Assert.NotNull(error);
    }
}

