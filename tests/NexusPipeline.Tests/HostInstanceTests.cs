using NexusPipeline.Services;
using Xunit;

namespace NexusPipeline.Tests;

/// <summary>宿主实例标识与重启交接标识的归一化语义。</summary>
public sealed class HostInstanceTests
{
    [Fact]
    public void Instance_identifier_is_a_stable_process_guid()
    {
        Assert.Matches("^[0-9a-f]{32}$", HostInstance.Id);
        Assert.Equal(HostInstance.Id, HostInstance.Id);
    }

    [Theory]
    [InlineData("9f1c2b3a4d5e6f708192a3b4c5d6e7f8", "9f1c2b3a4d5e6f708192a3b4c5d6e7f8")]
    [InlineData("  9f1c2b3a4d5e6f708192a3b4c5d6e7f8  ", "9f1c2b3a4d5e6f708192a3b4c5d6e7f8")]
    public void Valid_handoff_identifier_is_kept(string input, string expected)
    {
        Assert.Equal(expected, HostInstance.NormalizeRestartHandoff(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("9f1c2b3a4d5e6f708192a3b4c5d6e7f8-9f1c2b3a4d5e6f708192a3b4c5d6e7f8")]
    [InlineData("handoff\u0000value")]
    public void Invalid_handoff_identifier_falls_back_to_plain_start(string? input)
    {
        Assert.Equal(string.Empty, HostInstance.NormalizeRestartHandoff(input));
    }
}
