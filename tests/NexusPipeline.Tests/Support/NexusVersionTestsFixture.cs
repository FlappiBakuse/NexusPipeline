using Xunit;
using NexusPipeline.Shared.Versioning;
namespace NexusPipeline.Tests.Support;


public sealed class NexusVersionTestsFixture
{
internal static NexusVersion V(string value)
    {
        Assert.True(NexusVersion.TryParse(value, out NexusVersion version));
        return version;
    }
internal static string FindProjectRoot()
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
