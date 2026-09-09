using System.Diagnostics;
using NexusPipeline.Cli;
using NexusPipeline.Localization;
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
            // 托盘使用 exe 内置品牌图标（侧边栏 N 徽章），提取失败回退系统默认图标。
            Icon = ExtractAppIcon(),
            Text = HostLocalization.TranslateNamed("tray.title", "NexusPipeline", locale: LocaleCatalog.HostLocale),
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
        // （P11）：轻量模式未启动 Web 服务，禁用「打开管理页面」避免打开 404 页面
        var openWebItem = new ToolStripMenuItem(
            HostLocalization.TranslateNamed("tray.open_web", "打开管理页面", locale: LocaleCatalog.HostLocale),
            null,
            (_, _) => OpenWeb());
        if (RuntimeContext.Instance.Settings.LightweightMode)
        {
            openWebItem.Enabled = false;
            openWebItem.ToolTipText = HostLocalization.TranslateNamed(
                "tray.lightweight_tooltip",
                "轻量运行模式未启动 Web 服务，请使用命令行管理菜单",
                locale: LocaleCatalog.HostLocale);
        }
        menu.Items.Add(openWebItem);
        menu.Items.Add(
            HostLocalization.TranslateNamed("tray.cli_menu", "命令行管理菜单", locale: LocaleCatalog.HostLocale),
            null,
            (_, _) => OpenConsole("manage"));
        menu.Items.Add(
            HostLocalization.TranslateNamed("tray.status", "查看状态", locale: LocaleCatalog.HostLocale),
            null,
            (_, _) => OpenConsole("status"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(
            HostLocalization.TranslateNamed("tray.exit", "退出 NexusPipeline", locale: LocaleCatalog.HostLocale),
            null,
            (_, _) =>
        {
            if (Bootstrap.TryRequestDirectExit())
            {
                _icon.Visible = false;
            }
        });
        return menu;
    }

    public static void OpenWeb()
    {
        // （P11）：轻量模式防御（双击图标同样走此入口）
        if (RuntimeContext.Instance.Settings.LightweightMode)
        {
            Logger.Warn(HostLocalization.TranslateNamed(
                "tray.lightweight_open_failed",
                "轻量运行模式未启动 Web 服务，无法打开管理页面（请使用命令行管理菜单）。",
                locale: LocaleCatalog.HostLocale));
            return;
        }
        // 用实际监听端口（设置页改端口未重启 / 启动时端口冲突自动 +1 时与 Settings.WebPort 不一致）。
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
            Logger.Warn(HostLocalization.TranslateNamed(
                "tray.open_browser_failed",
                $"打开浏览器失败：{ex.Message}",
                new Dictionary<string, object?> { ["detail"] = ex.Message },
                LocaleCatalog.HostLocale));
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
            Logger.Warn(HostLocalization.TranslateNamed(
                "tray.open_console_failed",
                $"打开命令行窗口失败：{ex.Message}",
                new Dictionary<string, object?> { ["detail"] = ex.Message },
                LocaleCatalog.HostLocale));
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
