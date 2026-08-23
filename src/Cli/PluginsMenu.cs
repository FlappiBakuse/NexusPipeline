using NexusPipeline.Plugins;
using NexusPipeline.Services;

namespace NexusPipeline.Cli;

/// <summary>插件子菜单：查看插件列表、启用/禁用。</summary>
internal static class PluginsMenu
{
    public static void Show(RuntimeContext ctx)
    {
        Ui.ClearScreen();
        Console.WriteLine("===== 插件 =====");
        foreach (PluginSummary plugin in ctx.Plugins.PluginSummaries)
        {
            bool enabled = ctx.Plugins.IsConfiguredEnabled(plugin.Name);
            Console.WriteLine($"  {plugin.DisplayName} v{plugin.Version} [{(enabled ? "配置启用" : "配置禁用")} / 运行态 {ctx.Plugins.GetRuntimeState(plugin.Name)}]");
            Console.WriteLine($"    {plugin.Description}");
        }
        Console.WriteLine();
        Console.WriteLine("1. 启用/禁用插件（输入插件名）");
        Console.WriteLine("0. 返回上级");
        string? choice = Ui.Prompt("请选择：");
        switch (choice?.Trim())
        {
            case "0":
            case null:
                return;
            case "1":
            {
                string? name = Ui.Prompt("输入插件名：");
                if (string.IsNullOrWhiteSpace(name))
                {
                    return;
                }
                bool enabled = ctx.Plugins.IsConfiguredEnabled(name.Trim());
                if (!ctx.Plugins.SetEnabled(name.Trim(), !enabled, Audit.Manage))
                {
                    Console.WriteLine($"[提示] 插件不存在：{name.Trim()}。");
                    break;
                }
                Console.WriteLine($"[完成] {name.Trim()} 已{(enabled ? "禁用（下次启动生效）" : "启用（下次启动生效）")}。");
                break;
            }
            default:
                Console.WriteLine("[提示] 无效选项。");
                break;
        }
    }
}
