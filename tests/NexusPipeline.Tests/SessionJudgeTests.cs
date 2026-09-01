using NexusPipeline.Models;
using NexusPipeline.Services;
using Xunit;

namespace NexusPipeline.Tests;

/// <summary>完成判定状态机（SessionJudge）：脚本模式忽略关键字（v0.6.4 语义对齐）、关键字 AND/OR、失败优先、脚本结果防抖。</summary>
public class SessionJudgeTests
{
    private static ScriptInstance MakeScript(Action<ScriptInstance>? configure = null)
    {
        var script = new ScriptInstance { Name = "测试脚本", RootPath = "C:\\", MainExe = "C:\\x.exe", ConfigPath = "C:\\cfg", LogPath = "C:\\log.txt" };
        configure?.Invoke(script);
        return script;
    }

    [Fact]
    public void ScriptMode_IgnoresKeywords_KeywordLinesNeverHit()
    {
        var judge = new SessionJudge(MakeScript(s =>
        {
            s.SuccessKeywords = "成功关键字";
            s.FailureKeywords = "失败关键字";
            s.JudgeScriptEnabled = true;
            s.JudgeScriptLanguage = "javascript";
            s.JudgeScript = "console.log('x');";
        }));

        Assert.True(judge.ScriptMode);
        Assert.True(judge.IsConfigured);
        Assert.Equal(SessionJudge.LineHit.None, judge.HandleLine("包含成功关键字的日志行"));
        Assert.Equal(SessionJudge.LineHit.None, judge.HandleLine("包含失败关键字的日志行"));
        Assert.False(judge.IsMarker);
        Assert.False(judge.IsFailure);
    }

    [Fact]
    public void ScriptMode_ApplyJudgeResult_StillWorks()
    {
        var judge = new SessionJudge(MakeScript(s =>
        {
            s.SuccessKeywords = "成功关键字";
            s.JudgeScriptEnabled = true;
            s.JudgeScriptLanguage = "javascript";
            s.JudgeScript = "console.log('x');";
        }));

        Assert.Equal(SessionJudge.JudgeOutcome.Success, judge.ApplyJudgeResult("success", "ok", "", [], _ => { }));
        Assert.True(judge.IsMarker);
        Assert.Contains("ok", judge.Reason ?? "");
    }

    [Fact]
    public void ApplyJudgeResult_RetainsSelectedScreenshotIdOnlyForAcceptedResult()
    {
        var judge = new SessionJudge(MakeScript(s =>
        {
            s.JudgeScriptEnabled = true;
            s.JudgeScript = "x";
        }));

        Assert.Equal(
            SessionJudge.JudgeOutcome.Success,
            judge.ApplyJudgeResult("success", "ok", "", [], _ => { }, "screenshot-1"));
        Assert.Equal("screenshot-1", judge.NotifyScreenshotId);
        Assert.Equal(
            SessionJudge.JudgeOutcome.None,
            judge.ApplyJudgeResult("success", "later", "", [], _ => { }, "screenshot-2"));
        Assert.Equal("screenshot-1", judge.NotifyScreenshotId);
    }

    [Fact]
    public void KeywordMode_LineGroup_And_Semantics()
    {
        var judge = new SessionJudge(MakeScript(s => s.SuccessKeywords = "任务完成,全部成功"));

        Assert.Equal(SessionJudge.LineHit.SuccessKeyword, judge.HandleLine("[INFO] 任务完成，全部成功！"));
        Assert.True(judge.IsMarker);
    }

    [Fact]
    public void KeywordMode_CrossLine_And_AccumulatesAcrossLines()
    {
        // v0.7.1+：组内 AND 跨整个日志——各关键字在不同行分别出现（间隔任意长）即命中成功。
        var judge = new SessionJudge(MakeScript(s => s.SuccessKeywords = "任务完成,全部成功"));

        Assert.Equal(SessionJudge.LineHit.None, judge.HandleLine("[INFO] 任务完成，部分失败"));
        Assert.False(judge.IsMarker);
        Assert.Equal(SessionJudge.LineHit.SuccessKeyword, judge.HandleLine("[INFO] 最终进度 100%，全部成功！"));
        Assert.True(judge.IsMarker);
    }

    [Fact]
    public void KeywordMode_CrossLine_And_OrderIndependent()
    {
        // 与出现顺序无关：后出现的词先命中，前出现的词后续补上。
        var judge = new SessionJudge(MakeScript(s => s.SuccessKeywords = "任务完成,全部成功"));

        Assert.Equal(SessionJudge.LineHit.None, judge.HandleLine("[INFO] 全部成功"));
        Assert.False(judge.IsMarker);
        Assert.Equal(SessionJudge.LineHit.SuccessKeyword, judge.HandleLine("[INFO] 任务完成"));
        Assert.True(judge.IsMarker);
    }

    [Fact]
    public void KeywordMode_CrossLine_And_MissingOneWord_NeverHit()
    {
        // 整个日志只有一个词出现 → 永不命中（进程退出判定失败）。
        var judge = new SessionJudge(MakeScript(s => s.SuccessKeywords = "任务完成,全部成功"));

        Assert.Equal(SessionJudge.LineHit.None, judge.HandleLine("[INFO] 任务完成，部分失败"));
        Assert.Equal(SessionJudge.LineHit.None, judge.HandleLine("[INFO] 任务完成，再次部分失败"));
        Assert.False(judge.IsMarker);
    }

    [Fact]
    public void KeywordMode_CrossLine_And_CaseInsensitive()
    {
        var judge = new SessionJudge(MakeScript(s => s.SuccessKeywords = "DONE,COMPLETED"));

        Assert.Equal(SessionJudge.LineHit.None, judge.HandleLine("task done"));
        Assert.Equal(SessionJudge.LineHit.SuccessKeyword, judge.HandleLine("all completed"));
        Assert.True(judge.IsMarker);
    }

    [Fact]
    public void KeywordMode_LineOr_SecondGroup_Hits()
    {
        var judge = new SessionJudge(MakeScript(s => s.SuccessKeywords = "任务完成,全部成功\n任务结束"));

        Assert.Equal(SessionJudge.LineHit.SuccessKeyword, judge.HandleLine("任务结束"));
        Assert.True(judge.IsMarker);
    }

    [Fact]
    public void KeywordMode_FailureBeforeSuccess_FailureWins()
    {
        var judge = new SessionJudge(MakeScript(s =>
        {
            s.SuccessKeywords = "完成";
            s.FailureKeywords = "失败";
        }));

        judge.HandleLine("任务失败");
        judge.HandleLine("任务完成");

        Assert.True(judge.IsFailure);
        Assert.True(judge.IsMarker);
    }

    [Fact]
    public void KeywordMode_SuccessBeforeFailure_NotFailure()
    {
        var judge = new SessionJudge(MakeScript(s =>
        {
            s.SuccessKeywords = "完成";
            s.FailureKeywords = "失败";
        }));

        judge.HandleLine("任务完成");
        judge.HandleLine("任务失败");

        Assert.False(judge.IsFailure);
        Assert.True(judge.IsMarker);
    }

    [Fact]
    public void KeywordMode_SameLine_UsesTextOrder_SuccessFirst()
    {
        var judge = new SessionJudge(MakeScript(s =>
        {
            s.SuccessKeywords = "完成";
            s.FailureKeywords = "失败";
        }));

        Assert.Equal(SessionJudge.LineHit.SuccessKeyword, judge.HandleLine("任务完成，随后失败"));
        Assert.False(judge.IsFailure);
        Assert.True(judge.IsMarker);
    }

    [Fact]
    public void KeywordMode_SameLine_UsesTextOrder_FailureFirst()
    {
        var judge = new SessionJudge(MakeScript(s =>
        {
            s.SuccessKeywords = "完成";
            s.FailureKeywords = "失败";
        }));

        Assert.Equal(SessionJudge.LineHit.FailureKeyword, judge.HandleLine("任务失败，随后完成"));
        Assert.True(judge.IsFailure);
    }

    [Fact]
    public void ApplyJudgeResult_Failure_TriggersReplace_OnceOnly()
    {
        int replaceCalls = 0;
        var judge = new SessionJudge(MakeScript(s => { s.JudgeScriptEnabled = true; s.JudgeScript = "x"; }));

        Assert.Equal(SessionJudge.JudgeOutcome.Failure, judge.ApplyJudgeResult("failed", "卡住", "", ["cfg.txt"], _ => replaceCalls++));
        Assert.Equal(1, replaceCalls);
        Assert.True(judge.IsFailure);
        Assert.Equal(SessionJudge.JudgeOutcome.None, judge.ApplyJudgeResult("failed", "再次失败", "", [], _ => replaceCalls++));
        Assert.Equal(1, replaceCalls);
    }

    [Fact]
    public void ApplyJudgeResult_FailureThenSuccess_FailureWins()
    {
        var judge = new SessionJudge(MakeScript(s => { s.JudgeScriptEnabled = true; s.JudgeScript = "x"; }));

        judge.ApplyJudgeResult("failed", "卡住", "", [], _ => { });
        Assert.True(judge.IsFailure);
        judge.ApplyJudgeResult("success", "恢复", "", [], _ => { });

        // 失败优先语义：失败先于成功命中 → 失败仍成立（成功不覆盖失败）；marker 同时成立（宿主按失败终止）
        Assert.True(judge.IsFailure);
        Assert.True(judge.IsMarker);
    }

    [Fact]
    public void ApplyJudgeResult_InvalidStatus_Ignored()
    {
        var judge = new SessionJudge(MakeScript(s => { s.JudgeScriptEnabled = true; s.JudgeScript = "x"; }));

        Assert.Equal(SessionJudge.JudgeOutcome.None, judge.ApplyJudgeResult("weird", "r", "", [], _ => { }));
        Assert.False(judge.IsMarker);
        Assert.False(judge.IsFailure);
    }

    [Fact]
    public void NoConfigured_ModeIsNone_NotConfigured()
    {
        var judge = new SessionJudge(MakeScript());

        Assert.False(judge.IsConfigured);
        Assert.False(judge.ScriptMode);
        Assert.Equal(SessionJudge.LineHit.None, judge.HandleLine("任意日志"));
    }
}
