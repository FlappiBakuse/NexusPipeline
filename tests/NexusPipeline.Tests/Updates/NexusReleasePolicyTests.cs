using Xunit;
using NexusPipeline.Modules.Updates;
using NexusPipeline.Shared.Versioning;
namespace NexusPipeline.Tests.Updates;


public sealed class NexusReleasePolicyTests
{

    [Theory]
    [InlineData("0.15.12", false, true)]
    [InlineData("0.15.12-rc.1", true, true)]
    [InlineData("1.0.0-beta.1", true, true)]
    [InlineData("1.0.0", false, false)]
    [InlineData("2.0.0-rc.1", true, true)]
    [InlineData("2.0.0", false, false)]
    public void ReleasePolicy_SeparatesSuffixFromGitHubClassification(
        string value,
        bool expectedSuffix,
        bool expectedGitHubPrerelease)
    {
        NexusVersion version = V(value);

        Assert.Equal(expectedSuffix, version.HasPrereleaseSuffix);
        Assert.Equal(expectedGitHubPrerelease, NexusReleasePolicy.RequiresGitHubPrerelease(version));
    }

    [Fact]
    public void ReleasePolicy_HidesAllMajorZeroVersionsFromStableChannel()
    {
        Assert.False(NexusReleasePolicy.IsVisibleInChannel(V("0.15.12"), "stable"));
        Assert.False(NexusReleasePolicy.IsVisibleInChannel(V("0.15.12-rc.1"), "stable"));
        Assert.True(NexusReleasePolicy.IsVisibleInChannel(V("0.15.12"), "prerelease"));
        Assert.True(NexusReleasePolicy.IsVisibleInChannel(V("1.0.0"), "stable"));
    }

    private static NexusVersion V(string value)
    {
        Assert.True(NexusVersion.TryParse(value, out NexusVersion version));
        return version;
    }

    private static string FindProjectRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "src", "NexusPipeline.csproj")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new InvalidOperationException("无法定位 NexusPipeline 项目根目录");
    }
}
