using NexusPipeline.Services;
using NexusPipeline.Utilities;

namespace NexusPipeline.Cli;

/// <summary>维护菜单：显式维护能力入口，默认不做任何自动清理。</summary>
internal static class MaintenanceMenu
{
    public static void Show(RuntimeContext ctx)
    {
        while (true)
        {
            Ui.ClearScreen();
            string[] options =
            {
                "1. 清理历史用户名目录（惰性遗留数据）",
                "0. 返回上级",
            };
            Ui.Block(new List<string> { "===== NexusPipeline 维护 =====", "选择操作：" }.Concat(options).ToList());
            string? choice = Ui.Prompt("请选择：");
            if (choice is null)
            {
                return;
            }
            switch (choice.Trim())
            {
                case "0":
                    return;
                case "1":
                    ShowLegacyPrune(ctx);
                    break;
                default:
                    Console.WriteLine("[提示] 无效选项。");
                    break;
            }
            Console.WriteLine();
            Console.Write("按回车继续...");
            if (Console.ReadLine() is null)
            {
                return;
            }
        }
    }

    private static void ShowLegacyPrune(RuntimeContext ctx)
    {
        Ui.ClearScreen();
        UserDataPruner pruner = ctx.Resolve<UserDataPruner>();
        IReadOnlyList<LegacyDataCandidate> candidates = pruner.FindCandidates();
        if (candidates.Count == 0)
        {
            Console.WriteLine("没有发现历史用户名目录（未绑定当前用户的遗留数据目录）。");
            return;
        }
        Console.WriteLine("发现以下历史用户名目录（未对应当前任何用户绑定，运行与恢复均会跳过）：");
        Console.WriteLine();
        for (int i = 0; i < candidates.Count; i++)
        {
            LegacyDataCandidate item = candidates[i];
            Console.WriteLine($"  [{i + 1}] 脚本 {item.ScriptId} / 用户键 {item.UserKey}（{item.ItemCount} 个条目）");
        }
        Console.WriteLine();
        string? input = Ui.Prompt("输入序号清理（多个用空格分隔），直接回车返回：");
        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }
        var indexes = new List<int>();
        foreach (string token in input.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(token, out int index) && index >= 1 && index <= candidates.Count)
            {
                indexes.Add(index - 1);
            }
        }
        if (indexes.Count == 0)
        {
            Console.WriteLine("[提示] 未识别到有效序号。");
            return;
        }
        Console.Write($"确认删除以上 {indexes.Count} 个遗留目录？（此操作不可恢复，输入 yes 确认）：");
        if (!string.Equals(Console.ReadLine()?.Trim(), "yes", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("已取消。");
            return;
        }
        foreach (int index in indexes)
        {
            LegacyDataCandidate item = candidates[index];
            PruneResult result = pruner.Prune(item.ScriptId, item.UserKey, Audit.Manage);
            if (result.Succeeded)
            {
                Console.WriteLine($"[OK] 已清理 脚本 {item.ScriptId} / 用户键 {item.UserKey}");
            }
            else
            {
                Console.WriteLine($"[跳过] 脚本 {item.ScriptId} / 用户键 {item.UserKey}：{result.Error}");
            }
        }
    }
}