using Xunit;
using NexusPipeline.Modules.Updates;
using NexusPipeline.Shared.Versioning;
namespace NexusPipeline.Tests.Updates;


public sealed class UpdatePolicyTests
{

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
