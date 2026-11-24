using System.Runtime.Versioning;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class MaaRetryTests
{
    [Fact(DisplayName = "Original MaaEnd Replays Safe Login After Failed Process Family Closure And Continues Failed And Proven Unstarted Business Tasks")]
    public Task RestartsEndPreparation() => MaaOriginalProcessRetryTests.RunAsync("maaend-unstarted");

    [Fact(DisplayName = "Original MaaStellaSora Replays Safe Login After Failed Process Family Closure And Continues Failed And Proven Unstarted Business Tasks")]
    public Task RestartsStellaPreparation() => MaaOriginalProcessRetryTests.RunAsync("maas-unstarted");

    [Fact(DisplayName = "Original MaaEnd Restarts Its PC Pretask Child After Failure And Retains Both Closed Process Families")]
    public Task RestartsPcPreparation() => MaaOriginalProcessRetryTests.RunAsync("maaend-pc");

    [Fact(DisplayName = "Original MaaStellaSora Replays Login And Continues Only Remaining Repetitions After Native Failure")]
    public Task ContinuesStellaRepetitions() => MaaOriginalProcessRetryTests.RunAsync("maas-repeat");
}
