using System.Diagnostics;
using NexusPipeline.ControlPlane.Cli;
using NexusPipeline.ControlPlane.Http;
using NexusPipeline.Modules.Settings;
using NexusPipeline.Modules.Settings.Persistence;
using NexusPipeline.Shared.Localization;
using NexusPipeline.Shared.Logging;

namespace NexusPipeline.Host.Tray;

internal class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly HostRuntime _runtime;

    public TrayApp(HostRuntime runtime)
    {
        _runtime = runtime;
        _icon = new NotifyIcon
        {
            // 托盘使用 exe 内置品牌图标（侧边栏 N 徽章），提取失败回退系统默认图标。
            Icon = ExtractAppIcon(),
            Text = HostLocalization.TranslateNamed("tray.title", "NexusPipeline", locale: LocaleCatalog.HostLocale),
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _icon.DoubleClick += (_, _) => OpenManagedWeb();
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
        var openWebItem = new ToolStripMenuItem(
            HostLocalization.TranslateNamed("tray.open_web", "打开管理页面", locale: LocaleCatalog.HostLocale),
            null,
            (_, _) => OpenManagedWeb());
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
            if (_runtime.Bootstrap.TryRequestDirectExit())
            {
                _icon.Visible = false;
            }
        });
        return menu;
    }

    private void OpenManagedWeb()
    {
        _runtime.Get<NexusPipeline.Host.Desktop.IDesktopHost>().ShowAsync("tray").GetAwaiter().GetResult();
    }

    public static void OpenWeb()
    {
        AppSettings settings = AppSettingsStore.Load(ConfigLoadMode.ReadOnly);
        int port = WebServer.Current?.Port
            ?? CliTransport.FindServicePort(settings.WebPort)
            ?? settings.WebPort;
        OpenWeb(port);
    }

    public static void OpenWeb(int port)
    {
        if (!NexusPipeline.Host.Desktop.ManagementBrowser.Open(new Uri($"http://127.0.0.1:{port}/")))
            Logger.Warn(HostLocalization.TranslateNamed("tray.open_browser_failed", "打开浏览器失败：{detail}",
                new Dictionary<string, object?> { ["detail"] = "browser_launch_failed" }, LocaleCatalog.HostLocale));
    }

    private static void OpenConsole(string args)
    {
        try
        {
            string exe = Process.GetCurrentProcess().MainModule?.FileName ?? "NexusPipeline.exe";
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
