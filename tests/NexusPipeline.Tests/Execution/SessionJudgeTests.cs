using Xunit;
using NexusPipeline.Modules.Execution.Judgement;
using NexusPipeline.Modules.Scripts;

namespace NexusPipeline.Tests.Execution;

/// <summary>完成判定状态机：脚本模式忽略关键字、关键字 AND/OR 与失败优先、脚本结果防抖。</summary>
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
    }

    [Fact]
    public void KeywordMode_GroupAndSemantics_AccumulateAcrossLines()
    {
        var judge = new SessionJudge(MakeScript(s => s.SuccessKeywords = "任务完成,全部成功"));

        // 组内 AND 跨整个日志：各关键字在不同行分别出现即命中，且与出现顺序无关。
        Assert.Equal(SessionJudge.LineHit.None, judge.HandleLine("[INFO] 全部成功"));
        Assert.False(judge.IsMarker);
        Assert.Equal(SessionJudge.LineHit.SuccessKeyword, judge.HandleLine("[INFO] 任务完成"));
        Assert.True(judge.IsMarker);
    }

    [Fact]
    public void KeywordMode_LineOr_SecondGroupHitsAndMissingWordNeverHits()
    {
        var judge = new SessionJudge(MakeScript(s => s.SuccessKeywords = "任务完成,全部成功\n任务结束"));
        Assert.Equal(SessionJudge.LineHit.SuccessKeyword, judge.HandleLine("任务结束"));

        var incomplete = new SessionJudge(MakeScript(s => s.SuccessKeywords = "任务完成,全部成功"));
        Assert.Equal(SessionJudge.LineHit.None, incomplete.HandleLine("[INFO] 任务完成，部分失败"));
        Assert.False(incomplete.IsMarker);
    }

    [Fact]
    public void KeywordMode_FailureWinsRegardlessOfOrder()
    {
        var failureFirst = new SessionJudge(MakeScript(s => { s.SuccessKeywords = "完成"; s.FailureKeywords = "失败"; }));
        failureFirst.HandleLine("任务失败");
        failureFirst.HandleLine("任务完成");
        Assert.True(failureFirst.IsFailure);

        var successFirst = new SessionJudge(MakeScript(s => { s.SuccessKeywords = "完成"; s.FailureKeywords = "失败"; }));
        successFirst.HandleLine("任务完成");
        successFirst.HandleLine("任务失败");
        Assert.False(successFirst.IsFailure);
    }

    [Fact]
    public void KeywordMode_SameLine_UsesTextOrder()
    {
        var successFirst = new SessionJudge(MakeScript(s => { s.SuccessKeywords = "完成"; s.FailureKeywords = "失败"; }));
        Assert.Equal(SessionJudge.LineHit.SuccessKeyword, successFirst.HandleLine("任务完成，随后失败"));

        var failureFirst = new SessionJudge(MakeScript(s => { s.SuccessKeywords = "完成"; s.FailureKeywords = "失败"; }));
        Assert.Equal(SessionJudge.LineHit.FailureKeyword, failureFirst.HandleLine("任务失败，随后完成"));
        Assert.True(failureFirst.IsFailure);
    }

    [Fact]
    public void ApplyJudgeResult_PartialIsTerminalAndDoesNotReplaceConfig()
    {
        int replaceCalls = 0;
        var judge = new SessionJudge(MakeScript(s => { s.JudgeScriptEnabled = true; s.JudgeScript = "x"; }));

        Assert.Equal(
            SessionJudge.JudgeOutcome.Partial,
            judge.ApplyJudgeResult("partial", "部分任务未完成", "部分通知", ["cfg.txt"], _ => replaceCalls++, "screenshot-1"));

        Assert.True(judge.IsMarker);
        Assert.False(judge.IsFailure);
        Assert.Equal("screenshot-1", judge.NotifyScreenshotId);
        Assert.Equal(0, replaceCalls);
        Assert.Equal(
            SessionJudge.JudgeOutcome.None,
            judge.ApplyJudgeResult("partial", "稍后结果", "", [], _ => replaceCalls++));
    }

    [Fact]
    public void ApplyJudgeResult_FailureTriggersReplaceOnceAndKeepsFailureFirstMarker()
    {
        int replaceCalls = 0;
        var judge = new SessionJudge(MakeScript(s => { s.JudgeScriptEnabled = true; s.JudgeScript = "x"; }));

        Assert.Equal(SessionJudge.JudgeOutcome.Failure, judge.ApplyJudgeResult("failed", "卡住", "", ["cfg.txt"], _ => replaceCalls++));
        Assert.Equal(1, replaceCalls);
        Assert.Equal(SessionJudge.JudgeOutcome.None, judge.ApplyJudgeResult("failed", "再次失败", "", [], _ => replaceCalls++));
        Assert.Equal(1, replaceCalls);

        Assert.Equal(SessionJudge.JudgeOutcome.Partial, judge.ApplyJudgeResult("partial", "后续局部完成", "", [], _ => { }));
        Assert.Equal(SessionJudge.JudgeOutcome.Partial, judge.MarkerOutcome);
        Assert.True(judge.IsFailure);
    }

    [Fact]
    public void ApplyJudgeResult_InvalidStatusIgnoredAndUnconfiguredJudgeNeverHits()
    {
        var judge = new SessionJudge(MakeScript(s => { s.JudgeScriptEnabled = true; s.JudgeScript = "x"; }));
        Assert.Equal(SessionJudge.JudgeOutcome.None, judge.ApplyJudgeResult("weird", "r", "", [], _ => { }));
        Assert.False(judge.IsMarker);

        var none = new SessionJudge(MakeScript());
        Assert.False(none.IsConfigured);
        Assert.False(none.ScriptMode);
        Assert.Equal(SessionJudge.LineHit.None, none.HandleLine("任意日志"));
    }
}
