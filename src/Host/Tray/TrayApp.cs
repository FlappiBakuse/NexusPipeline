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
    private ToolStripMenuItem? _lightweightItem;
    private bool _changing;

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
        _lightweightItem = new ToolStripMenuItem(Text("tray.lightweight", "轻量模式")) { Checked = _runtime.Desktop.SavedLightweightMode };
        _lightweightItem.Click += async (_, _) => await ChangeLightweightModeAsync();
        menu.Items.Add(_lightweightItem);
        menu.Items.Add(Text("tray.reload_page", "重启页面"), null, async (_, _) =>
        {
            if (_changing) return;
            if (!_runtime.Desktop.SavedLightweightMode && !Confirm("tray.reload_confirm", "重启当前管理页面会丢失未保存的输入。继续吗？")) return;
            string result = await _runtime.Desktop.ReloadPageAsync();
            if (result is not ("reloading" or "requested")) ShowFailure(result);
        });
        menu.Opening += (_, _) => _lightweightItem.Checked = _runtime.Desktop.SavedLightweightMode;
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
        _ = _runtime.Desktop.ShowAsync("tray");
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

    private async Task ChangeLightweightModeAsync()
    {
        if (_changing || _lightweightItem is null) return;
        bool value = !_runtime.Desktop.SavedLightweightMode;
        if (value && !Confirm("tray.lightweight_confirm", "启用轻量模式会关闭当前桌面窗口并丢失未保存的输入；后端任务继续运行。继续吗？")) return;
        _changing = true;
        _lightweightItem.Enabled = false;
        try
        {
            string result = await _runtime.Desktop.SetLightweightModeAsync(value);
            if (result != "saved") ShowFailure(result);
        }
        finally
        {
            _lightweightItem.Checked = _runtime.Desktop.SavedLightweightMode;
            _lightweightItem.Enabled = true;
            _changing = false;
        }
    }

    private static string Text(string key, string fallback) => HostLocalization.TranslateNamed(key, fallback, locale: LocaleCatalog.HostLocale);
    private static bool Confirm(string key, string fallback) => MessageBox.Show(Text(key, fallback), "NexusPipeline", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.OK;
    private static void ShowFailure(string result) => MessageBox.Show(Text("tray.page_result." + result, "操作未完成，请稍后重试。"), "NexusPipeline", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icon.Dispose();
        }
        base.Dispose(disposing);
    }
}
