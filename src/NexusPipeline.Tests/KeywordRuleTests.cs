using NexusPipeline.Utilities;
using Xunit;

namespace NexusPipeline.Tests;

/// <summary>关键字规则（KeywordRule）：逗号分组 AND（跨日志累积语义由 SessionJudge 维护）、换行 OR、空行忽略。</summary>
public class KeywordRuleTests
{
    [Fact]
    public void Parse_EmptyOrBlank_ReturnsEmpty()
    {
        Assert.Empty(KeywordRule.Parse(""));
        Assert.Empty(KeywordRule.Parse("   \n  \n"));
    }

    [Fact]
    public void Parse_SplitsLines_AndCommaGroups()
    {
        var groups = KeywordRule.Parse("完成,成功\n结束");

        Assert.Equal(2, groups.Count);
        Assert.Equal(["完成", "成功"], groups[0]);
        Assert.Equal(["结束"], groups[1]);
    }

    [Fact]
    public void Parse_ChineseComma_AlsoSplits()
    {
        var groups = KeywordRule.Parse("完成，成功");

        Assert.Single(groups);
        Assert.Equal(["完成", "成功"], groups[0]);
    }

    [Fact]
    public void Parse_IgnoresEmptyLines()
    {
        var groups = KeywordRule.Parse("完成\n\n结束\n");

        Assert.Equal(2, groups.Count);
    }
}
