using NexusPipeline.Modules.Updates;
using NexusPipeline.Shared.Versioning;
using Xunit;

namespace NexusPipeline.Tests.Updates;

public sealed class UpdatePolicyTests
{
    [Fact]
    public void BreakingVersions_RequireSeparateBarriersAndPreserveOrdinaryUpdates()
    {
        const string firstBarrier = """
            {"schemaVersion":1,"repository":"FlappiBakuse/NexusPipeline","barriers":[
              {"version":"0.16.15","code":"net10-plugin-api2-cleanup","migrationUrl":"https://github.com/FlappiBakuse/NexusPipeline/blob/main/docs/user/README.md"}
            ]}
            """;
        const string bothBarriers = """
            {"schemaVersion":1,"repository":"FlappiBakuse/NexusPipeline","barriers":[
              {"version":"0.16.15","code":"net10-plugin-api2-cleanup"},
              {"version":"0.17.0","code":"global-refactor"}
            ]}
            """;
        (string Json, string Current, string Target, string? Expected)[] cases =
        [
            (firstBarrier, "0.16.14", "0.16.15", "0.16.15"),
            (firstBarrier, "0.16.14", "0.16.16", "0.16.15"),
            (bothBarriers, "0.16.14", "0.17.0", "0.16.15"),
            (firstBarrier, "0.16.15", "0.16.16", null),
            (firstBarrier, "0.16.15", "0.17.0", null),
            (bothBarriers, "0.16.15", "0.17.0", "0.17.0"),
            (bothBarriers, "0.17.0", "0.17.1", null),
            (firstBarrier, "0.16.14", "0.16.15-beta.1", null),
            (bothBarriers, "0.16.15", "0.16.15", null),
            (bothBarriers, "0.17.0", "0.16.15", null),
        ];
        foreach (var item in cases)
        {
            Assert.True(UpdatePolicy.TryParse(item.Json, out var policy, out var error), error);
            Assert.True(NexusVersion.TryParse(item.Current, out var current));
            Assert.True(NexusVersion.TryParse(item.Target, out var target));
            Assert.Equal(item.Expected, UpdatePolicy.FindBarrier(policy!, current, target)?.Version.ToString());
        }
    }
}
