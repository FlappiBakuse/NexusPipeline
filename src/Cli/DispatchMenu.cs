using NexusPipeline.Models;
using NexusPipeline.Services;
namespace NexusPipeline.Cli;

/// <summary>调度中心子菜单：手动执行脚本/队列、取消运行。</summary>
internal static class DispatchMenu
{
    public static void Show(RuntimeContext ctx)
    {
        Ui.ClearScreen();
        var lines = new List<string>
        {
            "===== 调度中心 =====",
        };
        IReadOnlyList<RunningExecution> active = ctx.Center.Active;
        if (active.Count == 0)
        {
            lines.Add("当前没有正在运行的任务。");
        }
        else
        {
            foreach (RunningExecution exec in active)
            {
                lines.Add($"[{exec.Id[..8]}] {exec.TargetName}（{exec.Kind}，{(exec.Mode == "auto" ? "自动" : "手动")}）当前：{exec.CurrentScriptName} {exec.CurrentStatus} 第 {exec.CurrentAttempt}/{exec.CurrentMaxAttempts} 次");
            }
        }
        lines.Add("");
        lines.Add("1. 手动执行脚本实例");
        lines.Add("2. 手动执行调度队列");
        lines.Add("3. 取消运行（输入运行 ID）");
        lines.Add("0. 返回上级");
        Ui.Block(lines);
        string? choice = Ui.Prompt("请选择：");
        switch (choice?.Trim())
        {
            case "0":
            case null:
                return;
            case "1":
            {
                if (ctx.Scripts.Count == 0)
                {
                    Console.WriteLine("[提示] 没有脚本实例。");
                    break;
                }
                for (int i = 0; i < ctx.Scripts.Count; i++)
                {
                    Console.WriteLine($"  {i + 1}. {ctx.Scripts[i].Name}");
                }
                string? number = Ui.Prompt("输入脚本编号：");
                if (int.TryParse(number?.Trim(), out int index) && index >= 1 && index <= ctx.Scripts.Count)
                {
                    try
                    {
                        ctx.Center.StartScript(ctx.Scripts[index - 1].Id, "manual", Audit.Manage);
                        Console.WriteLine("[完成] 已开始执行，进度见运行日志。");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[错误] {ex.Message}");
                    }
                }
                break;
            }
            case "2":
            {
                if (ctx.Queues.Count == 0)
                {
                    Console.WriteLine("[提示] 没有调度队列。");
                    break;
                }
                for (int i = 0; i < ctx.Queues.Count; i++)
                {
                    Console.WriteLine($"  {i + 1}. {ctx.Queues[i].Name}");
                }
                string? number = Ui.Prompt("输入队列编号：");
                if (int.TryParse(number?.Trim(), out int index) && index >= 1 && index <= ctx.Queues.Count)
                {
                    DispatchQueue queue = ctx.Queues[index - 1];
                    string? blocked = DispatchCenter.QueueBlockedBy(queue);
                    if (blocked is not null)
                    {
                        Console.WriteLine($"[错误] 队列「{queue.Name}」引用的脚本「{blocked}」正在运行，请先退出后再执行。");
                        break;
                    }
                    try
                    {
                        ctx.Center.StartQueue(queue.Id, "manual", Audit.Manage);
                        Console.WriteLine("[完成] 已开始执行，进度见运行日志。");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[错误] {ex.Message}");
                    }
                }
                break;
            }
            case "3":
            {
                string? runId = Ui.Prompt("输入运行 ID（前 8 位即可）：");
                RunningExecution? exec = active.FirstOrDefault(item => item.Id.StartsWith(runId?.Trim() ?? "", StringComparison.OrdinalIgnoreCase));
                if (exec is null)
                {
                    Console.WriteLine("[提示] 未找到该运行任务。");
                }
                else
                {
                    ctx.Center.Cancel(exec.Id, Audit.Manage);
                    Console.WriteLine("[完成] 已发送取消请求。");
                }
                break;
            }
            default:
                Console.WriteLine("[提示] 无效选项。");
                break;
        }
    }
}
