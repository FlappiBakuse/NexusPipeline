using System.Diagnostics;
using Microsoft.Win32;

namespace NexusPipeline;

public static class TaskRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private const string ValueName = "NexusPipeline";

    public static bool IsRegistered()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            string? current = key?.GetValue(ValueName) as string;
            return !string.IsNullOrWhiteSpace(current);
        }
        catch
        {
            return false;
        }
    }

    public static void Register()
    {
        try
        {
            string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (string.IsNullOrWhiteSpace(exePath))
            {
                Logger.Log("[错误] 无法确定主程序路径。");
                return;
            }
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            key.SetValue(ValueName, $"\"{exePath}\"");
            Audit.Log(Audit.System, "注册开机自启动", exePath);
        }
        catch (Exception ex)
        {
            Logger.Log($"[错误] 注册开机自启动失败：{ex.Message}");
        }
    }

    public static void Unregister()
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName);
                Audit.Log(Audit.System, "取消开机自启动");
            }
            else
            {
                Logger.Log("[提示] 未注册开机自启动。");
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"[错误] 取消开机自启动失败：{ex.Message}");
        }
    }

    public static void SyncWithSettings(AppSettings settings)
    {
        if (settings.AutoStart)
        {
            if (!IsRegistered())
            {
                Register();
            }
        }
        else if (IsRegistered())
        {
            Unregister();
        }
    }
}
