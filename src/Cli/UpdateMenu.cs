using System.Text.Json.Nodes;
using NexusPipeline.Services;
using NexusPipeline.Utilities;

namespace NexusPipeline.Cli;

/// <summary>更新子菜单：检查 / 下载 / 应用，经 CliTransport 复用常驻服务 HTTP 通道（Web 端可见同一状态机）。</summary>
internal static class UpdateMenu
{
    public static void Show(RuntimeContext ctx)
    {
        while (true)
        {
            Ui.ClearScreen();
            string[] options =
            {
                "1. 检查更新",
                "2. 下载更新（校验后就绪）",
                "3. 应用更新",
                "4. 取消下载",
                "0. 返回上级",
            };
            Ui.Block(new List<string> { "===== NexusPipeline 更新 =====", "选择操作：" }.Concat(options).ToList());
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
                    CheckViaService(ctx);
                    break;
                case "2":
                    DownloadViaService(ctx);
                    break;
                case "3":
                    ApplyViaService(ctx);
                    break;
                case "4":
                    CancelViaService(ctx);
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

    private static int? ServicePort(RuntimeContext ctx)
    {
        int? port = CliTransport.EnsureService();
        if (port is null)
        {
            Console.WriteLine("[错误] 未连接到常驻服务，无法执行更新操作。");
        }
        return port;
    }

    private static void CheckViaService(RuntimeContext ctx)
    {
        int? port = ServicePort(ctx);
        if (port is null)
        {
            return;
        }
        try
        {
            HttpResponseMessage resp = CliTransport.Post(port.Value, "/api/update/check", new { });
            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"[错误] {CliTransport.ReadError(resp)}");
                return;
            }
            JsonNode? node = JsonNode.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            Console.WriteLine($"当前版本：v{node?["current"]?.ToString()}");
            Console.WriteLine($"更新渠道：{node?["channel"]?.ToString()}");
            if (node?["available"]?.GetValue<bool>() == true)
            {
                Console.WriteLine($"发现新版本：v{node["latest"]?.ToString()}{(node["prerelease"]?.GetValue<bool>() == true ? "（Pre-release）" : "")}");
            }
            else
            {
                Console.WriteLine("当前已是最新版本。");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[错误] 检查更新失败：{ex.Message}");
        }
    }

    private static void DownloadViaService(RuntimeContext ctx)
    {
        int? port = ServicePort(ctx);
        if (port is null)
        {
            return;
        }
        try
        {
            HttpResponseMessage resp = CliTransport.Post(port.Value, "/api/update/download", new { });
            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"[错误] {CliTransport.ReadError(resp)}");
                return;
            }
            Console.WriteLine("正在下载并校验更新包...");
            DateTime deadline = DateTime.Now.AddMinutes(15);
            while (DateTime.Now < deadline)
            {
                Thread.Sleep(1000);
                HttpResponseMessage statusResp = CliTransport.Get(port.Value, "/api/update/status");
                JsonNode? node = JsonNode.Parse(statusResp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                string state = node?["state"]?.ToString() ?? "idle";
                if (node?["progress"] is not null)
                {
                    Console.Write($"\r下载进度：{node["progress"]?.ToString()}%  ");
                }
                if (state == "ready")
                {
                    Console.WriteLine();
                    Console.WriteLine($"[OK] 更新已就绪（v{node?["latest"]?.ToString()}），可执行「3. 应用更新」。");
                    return;
                }
                if (state == "idle" && !string.IsNullOrEmpty(node?["error"]?.ToString()))
                {
                    Console.WriteLine();
                    Console.WriteLine($"[错误] 下载失败：{node?["error"]?.ToString()}");
                    return;
                }
                if (state == "idle" && node?["available"]?.GetValue<bool>() != true)
                {
                    Console.WriteLine();
                    Console.WriteLine("[提示] 未发现可用更新，下载已取消。");
                    return;
                }
            }
            Console.WriteLine();
            Console.WriteLine("[错误] 下载超时（15 分钟），请查看管理器日志。");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[错误] 下载更新失败：{ex.Message}");
        }
    }

    private static void ApplyViaService(RuntimeContext ctx)
    {
        int? port = ServicePort(ctx);
        if (port is null)
        {
            return;
        }
        try
        {
            HttpResponseMessage resp = CliTransport.Post(port.Value, "/api/update/apply", new { defer = false });
            if (resp.IsSuccessStatusCode)
            {
                Console.WriteLine("[OK] 更新应用已启动，服务即将重启（连接会短暂断开）。");
                return;
            }
            JsonNode? node = JsonNode.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            string? code = node?["code"]?.ToString();
            string error = node?["error"]?.ToString() ?? CliTransport.ReadError(resp);
            if (code == "busy")
            {
                Console.WriteLine($"[提示] {error}");
                Console.Write("是否登记「下次启动更新」（退出后下次启动自动应用）？(y/N)：");
                if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("已取消。");
                    return;
                }
                HttpResponseMessage deferResp = CliTransport.Post(port.Value, "/api/update/apply", new { defer = true });
                if (deferResp.IsSuccessStatusCode)
                {
                    Console.WriteLine("[OK] 已登记：下次启动服务时自动应用更新。");
                }
                else
                {
                    Console.WriteLine($"[错误] {CliTransport.ReadError(deferResp)}");
                }
                return;
            }
            Console.WriteLine($"[错误] {error}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[错误] 应用更新失败：{ex.Message}");
        }
    }

    private static void CancelViaService(RuntimeContext ctx)
    {
        int? port = ServicePort(ctx);
        if (port is null)
        {
            return;
        }
        try
        {
            HttpResponseMessage resp = CliTransport.Post(port.Value, "/api/update/cancel", new { });
            JsonNode? node = JsonNode.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            Console.WriteLine(node?["ok"]?.GetValue<bool>() == true ? "[OK] 已取消下载。" : "[提示] 当前没有可取消的下载。");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[错误] 取消失败：{ex.Message}");
        }
    }
}