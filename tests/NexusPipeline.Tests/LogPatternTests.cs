using NexusPipeline.Persistence;
using Xunit;

namespace NexusPipeline.Tests;

/// <summary>日志路径格式解析（LogPattern）：精确文件 / 目录取最新 / 日期占位符 / 通配取最新修改。</summary>
public class LogPatternTests : IDisposable
{
    private readonly string _dir;

    public LogPatternTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-logpattern-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void ResolveFile_ExactFile_Exists()
    {
        string file = Path.Combine(_dir, "log.txt");
        File.WriteAllText(file, "x");

        Assert.Equal(file, LogPattern.ResolveFile(file));
    }

    [Fact]
    public void ResolveFile_ExactFile_Missing_ReturnsNull()
    {
        Assert.Null(LogPattern.ResolveFile(Path.Combine(_dir, "nope.log")));
    }

    [Fact]
    public void ResolveFile_Empty_ReturnsNull()
    {
        Assert.Null(LogPattern.ResolveFile(""));
        Assert.Null(LogPattern.ResolveFile("   "));
    }

    [Fact]
    public void ResolveFile_Directory_ReturnsLatestFile()
    {
        string old = Path.Combine(_dir, "a.log");
        string latest = Path.Combine(_dir, "b.log");
        File.WriteAllText(old, "old");
        Thread.Sleep(20);
        File.WriteAllText(latest, "new");

        Assert.Equal(latest, LogPattern.ResolveFile(_dir));
    }

    [Fact]
    public void ResolveFile_Wildcard_ReturnsLatestMatch()
    {
        string old = Path.Combine(_dir, "run-1.log");
        string latest = Path.Combine(_dir, "run-2.log");
        File.WriteAllText(old, "old");
        Thread.Sleep(20);
        File.WriteAllText(latest, "new");

        Assert.Equal(latest, LogPattern.ResolveFile(Path.Combine(_dir, "run-*.log")));
    }

    [Fact]
    public void ResolveFile_Wildcard_NoMatch_ReturnsNull()
    {
        Assert.Null(LogPattern.ResolveFile(Path.Combine(_dir, "nope-*.log")));
    }

    [Fact]
    public void ResolveFile_DateToken_TodayFile()
    {
        string name = DateTime.Now.ToString("yyyy-MM-dd");
        string file = Path.Combine(_dir, $"run-{name}.log");
        File.WriteAllText(file, "x");

        Assert.Equal(file, LogPattern.ResolveFile(Path.Combine(_dir, "run-{YYYY-MM-DD}.log")));
    }

    [Fact]
    public void ResolveFile_DateToken_Missing_ReturnsNull()
    {
        string name = DateTime.Now.ToString("yyyyMMdd");
        string other = Path.Combine(_dir, $"other-{name}.log");
        File.WriteAllText(other, "x");

        Assert.Null(LogPattern.ResolveFile(Path.Combine(_dir, "run-{YYYYMMDD}.log")));
    }
}
