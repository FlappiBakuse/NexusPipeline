using System.Diagnostics;
using NexusPipeline.Cli;
using NexusPipeline.Utilities;
using NexusPipeline.Web;

namespace NexusPipeline;

internal class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _icon;

    public TrayApp()
    {
        _icon = new NotifyIcon
        {
            // v0.8.7：托盘使用 exe 内置品牌图标（侧边栏 N 徽章），提取失败回退系统默认图标。
            Icon = ExtractAppIcon(),
            Text = "NexusPipeline 枢链",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _icon.DoubleClick += (_, _) => OpenWeb();
    }

    private static Icon ExtractAppIcon()
    {
        try
        {
            string exe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            return string.IsNullOrEmpty(exe)
                ? System.Drawing.SystemIcons.Application
                : System.Drawing.Icon.ExtractAssociatedIcon(exe) ?? System.Drawing.SystemIcons.Application;
        }
        catch
        {
            return System.Drawing.SystemIcons.Application;
        }
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        // v0.6.9+（P11）：轻量模式未启动 Web 服务，禁用「打开管理页面」避免打开 404 页面
        var openWebItem = new ToolStripMenuItem("打开管理页面", null, (_, _) => OpenWeb());
        if (RuntimeContext.Instance.Settings.LightweightMode)
        {
            openWebItem.Enabled = false;
            openWebItem.ToolTipText = "轻量运行模式未启动 Web 服务，请使用「命令行管理菜单」";
        }
        menu.Items.Add(openWebItem);
        menu.Items.Add("命令行管理菜单", null, (_, _) => OpenConsole("manage"));
        menu.Items.Add("查看状态", null, (_, _) => OpenConsole("status"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出 NexusPipeline", null, (_, _) =>
        {
            _icon.Visible = false;
            Application.Exit();
        });
        return menu;
    }

    public static void OpenWeb()
    {
        // v0.6.9+（P11）：轻量模式防御（双击图标同样走此入口）
        if (RuntimeContext.Instance.Settings.LightweightMode)
        {
            Logger.Warn("[警告] 轻量运行模式未启动 Web 服务，无法打开管理页面（请使用「命令行管理菜单」）。");
            return;
        }
        // v0.7.1+（KN-51）：用实际监听端口（设置页改端口未重启 / 启动时端口冲突自动 +1 时与 Settings.WebPort 不一致）。
        int port = WebServer.Current?.Port
            ?? CliTransport.FindServicePort(RuntimeContext.Instance.Settings.WebPort)
            ?? RuntimeContext.Instance.Settings.WebPort;
        OpenWeb(port);
    }

    public static void OpenWeb(int port)
    {
        try
        {
            Process.Start(new ProcessStartInfo($"http://127.0.0.1:{port}/")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 打开浏览器失败：{ex.Message}");
        }
    }

    private static void OpenConsole(string args)
    {
        try
        {
            string exe = Process.GetCurrentProcess().MainModule?.FileName ?? "nexus-pipeline.exe";
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"\"{exe}\" {args}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 打开命令行窗口失败：{ex.Message}");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icon.Dispose();
        }
        base.Dispose(disposing);
    }
}
