using System.Diagnostics;

namespace NexusPipeline;

internal class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _icon;

    public TrayApp()
    {
        _icon = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "NexusPipeline 枢链",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _icon.DoubleClick += (_, _) => OpenWeb();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开管理页面", null, (_, _) => OpenWeb());
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
        OpenWeb(RuntimeContext.Instance.Settings.WebPort);
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
