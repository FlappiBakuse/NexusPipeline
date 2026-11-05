using System.Diagnostics;
using System.Text.Json;

internal static class BaahOutput
{
    internal static async Task<int> RunAsync(string configuration, string log, bool failFirst = true)
    {
        OwnedFixtureEvidence.RequirePath(configuration); OwnedFixtureEvidence.RequirePath(log);
        using var document = JsonDocument.Parse(File.ReadAllBytes(configuration));
        var config = document.RootElement;
        var pipeline = config.GetProperty("TASK_ORDER_GROUP").GetProperty("ALL_PIPELINES")[0];
        var flags = pipeline.GetProperty("TASK_ONOFF").EnumerateArray().Select(item => item.GetBoolean()).ToArray();
        var automatic = config.GetProperty("OPEN_GAME_APP_TASK").GetBoolean();
        var cleanup = config.GetProperty("DO_POST_ALL_TASK").GetBoolean();
        var retry = File.Exists(log + ".attempts");
        var softwarePath = Path.Combine(Path.GetDirectoryName(configuration)!, "software.json");
        OwnedFixtureEvidence.RequirePath(softwarePath);
        using var software = JsonDocument.Parse(File.ReadAllBytes(softwarePath));
        var progress = software.RootElement.GetProperty("fixture_progress").GetInt32();
        File.AppendAllText(log + ".software", progress + "\n");
        if (retry)
        {
            foreach (var id in File.ReadAllLines(log + ".processes").Select(int.Parse))
                try { using var previous = Process.GetProcessById(id); if (!previous.HasExited) return 81; }
                catch (ArgumentException) { }
            if (!flags[0] || flags[1] || !flags[2] || !cleanup || progress != 1) return 82;
            File.WriteAllText(log + ".restart-verified", "previous family stopped; login enabled; completed cafe disabled");
        }
        File.AppendAllText(log + ".attempts", JsonSerializer.Serialize(new { automatic, cleanup, flags }) + "\n");
        var lines = new List<string>();
        void Info(string value, string level = "INFO") => lines.Add($"2.5.1 - 00:01 - {level} : {value}");
        void Flow(string name, bool failed = false)
        {
            Info($"判断任务{name}是否可以执行"); Info($"执行任务{name}");
            if (failed) Info("受控错误", "ERROR");
            Info($"判断任务{name}执行结果是否可控"); Info($"任务{name}执行结束");
        }
        Info("当前激活: pipeline 1");
        if (automatic) Flow("EnterGame");
        if (flags[0]) Flow("EnterGame");
        if (flags[1]) Flow("InCafe");
        if (flags[2]) Flow("CollectMails", !retry && failFirst);
        if (cleanup) Flow("PostAllTask");
        await File.AppendAllTextAsync(log, string.Join("\n", lines) + "\n");
        if (!failFirst)
        {
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(log + ".release"))
            { if (deadline.Elapsed > TimeSpan.FromSeconds(30)) return 84; await Task.Delay(25); }
            await File.AppendAllTextAsync(log, "BAAH_TEST_SUCCESS\n");
            return 0;
        }
        if (retry) { await File.AppendAllTextAsync(log, "BAAH_TEST_SUCCESS\n"); return 0; }
        await File.WriteAllTextAsync(softwarePath, "{\"SAVE_LOG_TO_FILE\":true,\"fixture_progress\":1}");
        var child = InheritedOutputChild.StartQuiet(Environment.ProcessPath!, log + ".game");
        File.WriteAllLines(log + ".processes", [Environment.ProcessId.ToString(), child.ToString()]);
        await Console.Out.WriteLineAsync("BAAH_TEST_FAILURE"); await Console.Out.FlushAsync();
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 83;
    }
}
