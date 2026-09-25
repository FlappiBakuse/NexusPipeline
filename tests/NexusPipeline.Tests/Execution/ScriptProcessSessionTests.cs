using System.Collections.Concurrent;
using System.Text;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.Scripts;
using Xunit;

namespace NexusPipeline.Tests.Execution;

public sealed class ScriptProcessSessionTests
{
    [Fact]
    public async Task MaaEndStdout_DecodesUtf8MultibyteAndFinalLineWithoutNewline()
    {
        Assert.True(OperatingSystem.IsWindows());
        const string expected = "任务启动失败: 未搜索到任何窗口";
        string command = "$b=[Text.Encoding]::UTF8.GetBytes('" + expected
            + "');[Console]::OpenStandardOutput().Write($b,0,$b.Length)";
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var script = new ScriptInstance { Name = "UTF-8 output probe", PluginType = "maaend" };
        var lines = new ConcurrentQueue<string>();
        using var session = ScriptProcessSession.Start(
            script,
            "测试",
            powershell,
            Path.GetTempPath(),
            new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded },
            null,
            null);
        session.AttachOutput((line, _) =>
        {
            if (line is not null) lines.Enqueue(line);
        });
        await session.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        await session.WaitForOutputDrainAsync(CancellationToken.None);
        Assert.Equal(0, session.Process.ExitCode);
        Assert.Contains(expected, lines);
    }
}
