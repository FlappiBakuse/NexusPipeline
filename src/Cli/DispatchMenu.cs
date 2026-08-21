using System.Text.Json.Nodes;
using NexusPipeline.Models;
using NexusPipeline.Services;
namespace NexusPipeline.Cli;

/// <summary>调度中心子菜单：手动执行脚本/队列、取消运行（v0.6.6+ 统一经常驻服务 HTTP 通道，
/// 与 CLI run-script/run-queue 同通道——Web 端可见运行任务，消除进程内直调与 HTTP 通道割裂）。</summary>
internal static class DispatchMenu
{
    public static void Show(RuntimeContext ctx)
    {
        Ui.ClearScreen();
        var lines = new List<string>
        {
            "===== 调度中心 =====",
        };
        int? port = CliTransport.EnsureService();
        if (port is null)
        {
            lines.Add("[错误] 无法连接常驻服务，调度中心不可用。");
            Ui.Block(lines);
            Console.Write("按回车返回...");
            Console.ReadLine();
            return;
        }
        var active = new List<JsonNode>();
        try
        {
            HttpResponseMessage resp = CliTransport.Get(port.Value, "/api/status");
            if (resp.IsSuccessStatusCode)
            {
                JsonNode? node = JsonNode.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                if (node?["running"] is JsonArray arr)
                {
                    active.AddRange(arr.Select(item => item!));
                }
            }
            else
            {
                lines.Add($"[警告] 查询运行状态失败（HTTP {(int)resp.StatusCode}）。");
            }
        }
        catch (Exception ex)
        {
            lines.Add($"[警告] 查询运行状态失败：{ex.Message}");
        }
        if (active.Count == 0)
        {
            lines.Add("当前没有正在运行的任务。");
        }
        else
        {
            foreach (JsonNode item in active)
            {
                string id = item["id"]?.ToString() ?? "";
                string shortId = id.Length > 8 ? id[..8] : id;
                string mode = item["mode"]?.ToString() == "auto" ? "自动" : "手动";
                lines.Add($"[{shortId}] {item["targetName"]?.ToString()}（{item["kind"]?.ToString()}，{mode}）当前：{item["currentScriptName"]?.ToString()} {item["currentStatus"]?.ToString()} 第 {item["currentAttempt"]?.ToString()}/{item["currentMaxAttempts"]?.ToString()} 次");
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
                    Console.WriteLine($"  {i + 1}. {ctx.Scripts.OrderBy(script => script.Index).ElementAt(i).Name}");
                }
                string? number = Ui.Prompt("输入脚本编号：");
                if (int.TryParse(number?.Trim(), out int index) && index >= 1 && index <= ctx.Scripts.Count)
                {
                    ScriptInstance script = ctx.Scripts.OrderBy(script => script.Index).ElementAt(index - 1);
                    Submit("script", new { scriptId = script.Id, mode = "manual" }, $"脚本「{script.Name}」已提交运行");
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
                    Console.WriteLine($"  {i + 1}. {ctx.Queues.OrderBy(queue => queue.Index).ElementAt(i).Name}");
                }
                string? number = Ui.Prompt("输入队列编号：");
                if (int.TryParse(number?.Trim(), out int index) && index >= 1 && index <= ctx.Queues.Count)
                {
                    DispatchQueue queue = ctx.Queues.OrderBy(queue => queue.Index).ElementAt(index - 1);
                    string? blocked = RuntimeContext.Instance.Validator.QueueBlockedBy(queue);
                    if (blocked is not null)
                    {
                        Console.WriteLine($"[错误] 队列「{queue.Name}」引用的脚本「{blocked}」正在运行，请先退出后再执行。");
                        break;
                    }
                    Submit("queue", new { queueId = queue.Id, mode = "manual" }, $"队列「{queue.Name}」已提交运行");
                }
                break;
            }
            case "3":
            {
                string? runId = Ui.Prompt("输入运行 ID（前 8 位即可）：");
                JsonNode? exec = active.FirstOrDefault(item => (item["id"]?.ToString() ?? "").StartsWith(runId?.Trim() ?? "", StringComparison.OrdinalIgnoreCase));
                if (exec is null)
                {
                    Console.WriteLine("[提示] 未找到该运行任务。");
                    break;
                }
                try
                {
                    int? port2 = CliTransport.EnsureService();
                    if (port2 is null)
                    {
                        break;
                    }
                    HttpResponseMessage resp = CliTransport.Post(port2.Value, "/api/cancel", new { runId = exec["id"]?.ToString() });
                    if (resp.IsSuccessStatusCode)
                    {
                        Console.WriteLine("[完成] 已发送取消请求。");
                    }
                    else
                    {
                        Console.WriteLine($"[错误] {CliTransport.ReadError(resp)}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[错误] 提交取消请求失败：{ex.Message}");
                }
                break;
            }
            default:
                Console.WriteLine("[提示] 无效选项。");
                break;
        }
    }

    /// <summary>经常驻服务 HTTP 提交任务（不阻塞轮询，与 Web 端一致）；返回后提示运行 ID。</summary>
    private static void Submit(string kind, object body, string successText)
    {
        try
        {
            int? port = CliTransport.EnsureService();
            if (port is null)
            {
                return;
            }
            HttpResponseMessage resp = CliTransport.Post(port.Value, $"/api/dispatch/{kind}", body);
            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"[错误] {CliTransport.ReadError(resp)}");
                return;
            }
            string runId = JsonNode.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult())?["runId"]?.ToString() ?? "";
            string shortId = runId.Length > 8 ? runId[..8] : runId;
            Console.WriteLine($"[完成] {successText}（运行 ID {shortId}，进度见运行日志/Web 界面）。");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[错误] 提交任务失败：{ex.Message}");
        }
    }
}
