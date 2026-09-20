using Xunit;
using NexusPipeline.Shared.Versioning;
namespace NexusPipeline.Tests.Shared;


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
