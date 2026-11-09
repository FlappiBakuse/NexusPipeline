using NexusPipeline.Platform.Storage;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class PathSyntaxTests
{
    [Theory(DisplayName = "Reusing The Same Path Text Cannot Hide A New Actual Live Or Broken Junction")]
    [InlineData(false)]
    [InlineData(true)]
    public void RechecksPhysicalJunctions(bool broken)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, Guid.NewGuid().ToString("N"));
        var parent = Path.Combine(root, "selected"); var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(parent); Directory.CreateDirectory(outside);
        var path = Path.Combine(parent, "evidence.txt"); var neighbor = Path.Combine(outside, "evidence.txt");
        File.WriteAllText(neighbor, "preserved outside bytes");
        Assert.Equal(path, PhysicalPathPolicy.NormalizeWithoutLinks(path));
        Directory.Delete(parent);
        ControlledJunction.Create(parent, outside);
        try
        {
            if (broken) { File.Delete(neighbor); Directory.Delete(outside); }
            Assert.Equal(path, PhysicalPathPolicy.NormalizeSyntax(path));
            var rejected = Assert.Throws<IOException>(() => PhysicalPathPolicy.NormalizeWithoutLinks(path));
            Assert.Equal("filesystem_reparse_point_rejected", rejected.Message);
            if (!broken) Assert.Equal("preserved outside bytes", File.ReadAllText(neighbor));
        }
        finally { Directory.Delete(parent); }
        Assert.Equal(path, PhysicalPathPolicy.NormalizeWithoutLinks(path));
    }

    [Fact(DisplayName = "Path Syntax Preserves Literal Case For Case Sensitive Locations")]
    public void PreservesLiteralCase()
    {
        var root = Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!;
        var upper = Path.Combine(root, "Owner 数据", "FILE.txt"); var lower = Path.Combine(root, "Owner 数据", "file.txt");
        Assert.Equal(upper, PhysicalPathPolicy.NormalizeSyntax(upper));
        Assert.Equal(lower, PhysicalPathPolicy.NormalizeSyntax(lower));
    }

    [Theory(DisplayName = "Device Names Alternate Streams And Invalid Components Are Rejected")]
    [InlineData("CON.txt", "unsupported_filesystem_component")]
    [InlineData("AUX", "unsupported_filesystem_component")]
    [InlineData("LPT1", "unsupported_filesystem_component")]
    [InlineData("nul", "filesystem_device_or_stream_path_rejected")]
    [InlineData("file:stream", "filesystem_device_or_stream_path_rejected")]
    [InlineData("bad*", "unsupported_filesystem_component")]
    public void RejectsUnsafeSyntax(string invalid, string reason)
    {
        var root = Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!;
        Assert.Equal(reason, Assert.Throws<IOException>(() => PhysicalPathPolicy.NormalizeSyntax(Path.Combine(root, invalid))).Message);
    }
}
