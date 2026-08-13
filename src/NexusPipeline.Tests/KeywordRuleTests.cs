using NexusPipeline.Utilities;
using Xunit;

namespace NexusPipeline.Tests;

/// <summary>关键字规则（KeywordRule）：行内逗号 AND、换行 OR、空行忽略。</summary>
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

    [Fact]
    public void LineHits_AndGroup_AllWordsRequired()
    {
        var groups = KeywordRule.Parse("完成,成功");

        Assert.True(KeywordRule.LineHits("任务完成，成功", groups));
        Assert.False(KeywordRule.LineHits("任务完成，失败", groups));
    }

    [Fact]
    public void LineHits_OrGroup_AnyGroupHits()
    {
        var groups = KeywordRule.Parse("完成,成功\n结束");

        Assert.True(KeywordRule.LineHits("任务结束", groups));
        Assert.True(KeywordRule.LineHits("任务完成，全部成功", groups));
        Assert.False(KeywordRule.LineHits("全部成功", groups));
        Assert.False(KeywordRule.LineHits("无关内容", groups));
    }

    [Fact]
    public void LineHits_EmptyGroups_ReturnsFalse()
    {
        Assert.False(KeywordRule.LineHits("任务完成", []));
        Assert.False(KeywordRule.LineHits("", KeywordRule.Parse("完成")));
    }

    [Fact]
    public void LineHits_CaseInsensitive()
    {
        var groups = KeywordRule.Parse("DONE");

        Assert.True(KeywordRule.LineHits("done", groups));
    }
}
