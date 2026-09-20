using Xunit;
using NexusPipeline.Modules.Configuration.Exchange;
namespace NexusPipeline.Tests.Configuration;


public sealed class ConfigRunSessionFinalizationTests
{

    [Fact]
    public void ConfigRunSession_FinalizeRun_IsIdempotent()
    {
        var session = new ConfigRunSession("script", userKey: null, configPath: "", hasJudgeScript: false);

        Assert.Null(session.FinalizeRun(autoUpdateConfig: true));
        Assert.Null(session.FinalizeRun(autoUpdateConfig: true));

        session = new ConfigRunSession("script", userKey: null, configPath: "", hasJudgeScript: false);
        session.MarkProcessCleanupUnconfirmed("测试保留现场");

        string? first = session.FinalizeRun(autoUpdateConfig: true);
        string? second = session.FinalizeRun(autoUpdateConfig: true);

        Assert.Equal(first, second);
        Assert.Contains("保留配置交换现场", first);
    }
}
