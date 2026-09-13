using NexusPipeline.Services.Update;
using NexusPipeline.Utilities;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class NexusVersionTests
{
    [Theory]
    [InlineData("0.0.0", "0.0.0")]
    [InlineData("1.2.3-beta.1", "1.2.3-beta.1")]
    [InlineData("1.2.3-rc.9", "1.2.3-rc.9")]
    [InlineData("V10.20.30", "10.20.30")]
    public void TryParseTag_UsesCurrentVersionGrammar(string value, string expected)
    {
        Assert.True(NexusVersion.TryParseTag(value, out NexusVersion version));
        Assert.Equal(expected, version.ToString());
    }

    [Theory]
    [InlineData("01.2.3")]
    [InlineData("1.2.3-alpha.1")]
    [InlineData("1.2.3-beta.01")]
    [InlineData("1.2.3+build.1")]
    [InlineData("v1.2.3")]
    [InlineData("1.2")]
    public void TryParse_RejectsVersionsOutsideCurrentGrammar(string value)
    {
        Assert.False(NexusVersion.TryParse(value, out _));
    }

    [Fact]
    public void Compare_OrdersBetaRcAndStableWithinCoreVersion()
    {
        Assert.True(V("1.2.3-beta.1").CompareTo(V("1.2.3-beta.2")) < 0);
        Assert.True(V("1.2.3-beta.2").CompareTo(V("1.2.3-rc.1")) < 0);
        Assert.True(V("1.2.3-rc.1").CompareTo(V("1.2.3")) < 0);
        Assert.True(V("1.2.3").CompareTo(V("1.2.4-beta.1")) < 0);
    }

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

    [Fact]
    public void UpdatePolicy_RejectsMalformedOrUntrustedDocuments()
    {
        Assert.False(UpdatePolicy.TryParse(
            "{\"schemaVersion\":1,\"repository\":\"other/repo\",\"barriers\":[]}",
            out _,
            out string? error));
        Assert.Contains("仓库", error);

        Assert.False(UpdatePolicy.TryParse(
            "{\"schemaVersion\":1,\"repository\":\"FlappiBakuse/NexusPipeline\",\"barriers\":[{\"version\":\"0.17.0\",\"code\":\"layout\"},{\"version\":\"0.16.0\",\"code\":\"older\"}]}",
            out _,
            out error));
        Assert.Contains("从旧到新", error);
    }

    [Fact]
    public void UpdatePolicy_FindsEarliestCrossedBarrier()
    {
        string json = """
        {
          "schemaVersion": 1,
          "repository": "FlappiBakuse/NexusPipeline",
          "barriers": [
            { "version": "0.16.0", "code": "layout-v2", "migrationUrl": "https://example.com/migrate" },
            { "version": "0.17.0", "code": "layout-v3" }
          ]
        }
        """;
        Assert.True(UpdatePolicy.TryParse(json, out UpdatePolicyDocument? policy, out string? error), error);

        UpdateBarrier? barrier = UpdatePolicy.FindBarrier(policy!, V("0.15.12"), V("0.18.0"));

        Assert.NotNull(barrier);
        Assert.Equal("0.16.0", barrier!.Version.ToString());
        Assert.Equal("layout-v2", barrier.Code);
        Assert.Equal("https://example.com/migrate", barrier.MigrationUrl);
    }

    [Fact]
    public void UpdatePolicy_RepositoryPolicy_IsValid()
    {
        string path = Path.Combine(FindProjectRoot(), "update-policy.json");
        Assert.True(File.Exists(path), $"缺少仓库根目录 update-policy.json：{path}");

        Assert.True(
            UpdatePolicy.TryParse(File.ReadAllText(path), out UpdatePolicyDocument? policy, out string? error),
            error);
        Assert.Equal(UpdatePolicy.Repository, policy!.Repository);
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
