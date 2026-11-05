using System.Diagnostics;
using System.Text.Json;

internal static class HookInterpreterFixture
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is ["child", var ready])
        {
            OwnedFixtureEvidence.RequirePath(ready);
            OwnedFixtureEvidence.WriteReport(ready, new { processId = Environment.ProcessId });
            await Task.Delay(Timeout.InfiniteTimeSpan); return 0;
        }
        if (args is not [var report, var role, var marker, var configuration, var inputKind, .. var values]) return 93;
        OwnedFixtureEvidence.RequirePath(report); OwnedFixtureEvidence.RequirePath(marker);
        if (configuration != "-") OwnedFixtureEvidence.RequirePath(configuration);
        if (inputKind == "batch")
        {
            if (!int.TryParse(Environment.GetEnvironmentVariable("NEXUS_HOOK_ARGUMENT_COUNT"), out var count) || count is < 0 or > 16) return 94;
            values = Enumerable.Range(0, count).Select(index => Environment.GetEnvironmentVariable("NEXUS_HOOK_ARGUMENT_" + index) ?? "").ToArray();
        }
        else if (inputKind == "powershell") values = [];
        else if (inputKind != "argv") return 95;
        var childReady = report + ".child";
        var child = InheritedOutputChild.StartQuiet(Environment.ProcessPath!, childReady);
        var timer = Stopwatch.StartNew();
        while (!File.Exists(childReady) && timer.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10);
        if (!File.Exists(childReady)) return 96;
        var code = await ExecutionHookFixture.RunAsync([role, marker, configuration, "0", "False"]);
        Console.Out.WriteLine("hook-stdout"); Console.Out.Flush();
        Console.Error.WriteLine("hook-stderr"); Console.Error.Flush();
        OwnedFixtureEvidence.WriteReport(report, new { arguments = values, inputKind, workingDirectory = Environment.CurrentDirectory,
            processId = Environment.ProcessId, childProcessId = child, imagePath = Environment.ProcessPath, code });
        return code;
    }
}
