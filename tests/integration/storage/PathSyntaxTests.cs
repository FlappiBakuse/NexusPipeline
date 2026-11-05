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
        finally
        {
            Assert.True((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0);
            Directory.Delete(parent);
        }
    }

    [Theory(DisplayName = "Reused Syntax Preserves Literal Case And Still Rejects Reserved Or Stream Components")]
    [InlineData("CON.txt")]
    [InlineData("AUX")]
    [InlineData("LPT1")]
    [InlineData("nul")]
    [InlineData("file:stream")]
    [InlineData("bad*")]
    public void PreservesLiteralSyntax(string invalid)
    {
        var root = Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!;
        var upper = Path.Combine(root, "Owner 数据", "FILE.txt"); var lower = Path.Combine(root, "Owner 数据", "file.txt");
        Assert.Equal(upper, PhysicalPathPolicy.NormalizeSyntax(upper));
        Assert.Equal(lower, PhysicalPathPolicy.NormalizeSyntax(lower));
        Assert.Equal(upper, PhysicalPathPolicy.NormalizeSyntax(upper));
        Assert.Throws<IOException>(() => PhysicalPathPolicy.NormalizeSyntax(Path.Combine(root, invalid)));
    }
}
