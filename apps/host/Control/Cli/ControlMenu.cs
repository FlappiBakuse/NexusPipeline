using System.Globalization;

namespace NexusPipeline.Host.Control.Cli;

internal static class ControlMenu
{
    private sealed record Entry(string Label, string Command, string[] Options, bool Body = false, bool Confirm = false);
    private static readonly (string Label, Entry[] Entries)[] Groups =
    [
        ("状态与诊断 / Status and diagnostics", [
            new("状态 / Status", "status", []), new("权限 / Capabilities", "capabilities", []),
            new("诊断 / Doctor", "doctor", []), new("操作结果 / Operation result", "operation", ["id"])]),
        ("脚本、用户与队列 / Scripts, users and queues", [
            new("定义列表 / Definitions", "automation", []),
            new("按 ID 或唯一名称解析 / Resolve ID or unique name", "resolve", ["kind", "value"]),
            new("变更定义 / Change definitions", "automation-change", [], true, true),
            new("导入预览 / Preview import", "automation-import-preview", [], true),
            new("应用导入 / Apply import", "automation-import-apply", [], true, true),
            new("发现模拟器实例 / Discover emulator instances", "emulator-discover", [], true)]),
        ("执行与调度 / Execution and scheduling", [
            new("调度 / Scheduling", "scheduling", []),
            new("执行预览 / Preview execution", "execution-preview", [], true),
            new("开始执行 / Start execution", "execution-start", ["id"], true, true),
            new("取消执行 / Cancel execution", "execution-cancel", ["id", "start-operation-id"], true, true)]),
        ("历史记录 / History", [new("记录 / Records", "records", []),
            new("执行尝试 / Attempts", "record-attempts", ["run-id"]),
            new("日志 / Logs", "record-logs", ["run-id", "attempt-id"]),
            new("图像 / Images", "record-images", ["run-id", "attempt-id"])]),
        ("设置与通知 / Settings and notifications", [
            new("设置 / Settings", "settings", []), new("保存设置 / Save settings", "settings-save", [], true, true),
            new("通知 / Notifications", "notifications", []),
            new("保存通知 / Save notification", "notification-save", [], true, true),
            new("测试通知 / Test notification", "notification-test", [], true, true),
            new("删除通知 / Delete notification", "notification-delete", [], true, true)]),
        ("插件 / Extensions", [new("插件 / Extensions", "extensions", []),
            new("目录 / Catalog", "extension-catalog", ["channel"]),
            new("刷新目录 / Refresh catalog", "extension-refresh", ["channel"]),
            new("安装、更新或移除 / Install, update or remove", "extension-apply", [], true, true),
            new("恢复 / Reconcile", "extension-reconcile", [], true, true),
            new("插件操作 / Extension operation", "extension-operation", ["id"])]),
        ("更新与维护 / Updates and maintenance", [
            new("更新源 / Feed", "maintenance-feed", ["channel"]),
            new("刷新更新源 / Refresh feed", "maintenance-refresh", ["channel"]),
            new("准备更新 / Prepare update", "maintenance-prepare", [], true, true),
            new("开始更新 / Start update", "maintenance-start", ["id"], false, true),
            new("取消更新 / Cancel update", "maintenance-cancel", ["id"], false, true),
            new("重启 Host / Restart Host", "host-restart", ["id"], false, true),
            new("维护结果 / Maintenance result", "maintenance-operation", ["id"])])
    ];

    internal static async Task<int> RunAsync(string[] args, TextReader? input = null, TextWriter? prompts = null,
        Func<string[], Task<int>>? execute = null)
    {
        input ??= Console.In; prompts ??= Console.Error; execute ??= ControlCli.RunAsync;
        try
        {
            // The normal parser remains the authority for endpoint and credential options.
            _ = ControlCliRequest.Parse(["status", .. args]);
        }
        catch (ArgumentException) { await prompts.WriteLineAsync("control_cli_input_invalid"); return 2; }
        while (true)
        {
            await prompts.WriteLineAsync("NexusPipeline 管理菜单 / Management menu");
            for (var index = 0; index < Groups.Length; index++)
                await prompts.WriteLineAsync($"{index + 1}. {Groups[index].Label}");
            var group = await Select(input, prompts, Groups.Length);
            if (group < 0) return 0;
            if (group == 0) continue;
            await ShowGroup(Groups[group - 1].Entries, args, input, prompts, execute);
        }
    }

    private static async Task ShowGroup(Entry[] entries, string[] common, TextReader input, TextWriter prompts,
        Func<string[], Task<int>> execute)
    {
        while (true)
        {
            for (var index = 0; index < entries.Length; index++)
                await prompts.WriteLineAsync($"{index + 1}. {entries[index].Label}");
            var selected = await Select(input, prompts, entries.Length);
            if (selected < 0) return;
            if (selected == 0) continue;
            var entry = entries[selected - 1];
            var command = new List<string> { entry.Command };
            command.AddRange(common);
            var canceled = false;
            foreach (var option in entry.Options.Concat(entry.Body ? new[] { "input" } : []))
            {
                await prompts.WriteLineAsync(option == "input"
                    ? "JSON 文件路径（空行取消） / JSON file path (blank cancels):"
                    : $"--{option}（空行取消 / blank cancels）:");
                var value = await input.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(value)) { canceled = true; break; }
                // Menu input and request stdin cannot share one stream without consuming later choices.
                if (option == "input" && value.Trim() == "-")
                { await prompts.WriteLineAsync("请使用文件 / Use a file"); canceled = true; break; }
                command.Add("--" + option); command.Add(value.Trim());
            }
            if (canceled) continue;
            if (entry.Confirm)
            {
                await prompts.WriteLineAsync("确认执行？输入 yes；其他输入取消 / Type yes to confirm:");
                if (await input.ReadLineAsync() != "yes") continue;
            }
            var exit = await execute(command.ToArray());
            await prompts.WriteLineAsync($"退出码 / Exit code: {exit}");
        }
    }

    private static async Task<int> Select(TextReader input, TextWriter prompts, int count)
    {
        await prompts.WriteLineAsync("0. 返回 / Back");
        await prompts.WriteAsync("> "); await prompts.FlushAsync();
        var value = await input.ReadLineAsync();
        if (value is null || value.Trim() == "0") return -1;
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var selected) && selected >= 1 && selected <= count)
            return selected;
        await prompts.WriteLineAsync("无效选项 / Invalid choice");
        return 0;
    }
}
