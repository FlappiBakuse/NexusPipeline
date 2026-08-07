using NexusPipeline.Plugins;

namespace NexusPipeline.Cli;

/// <summary>插件子菜单：查看插件列表、启用/禁用。</summary>
internal static class PluginsMenu
{
    public static void Show(RuntimeContext ctx)
    {
        Ui.ClearScreen();
        Console.WriteLine("===== 插件 =====");
        foreach (IPlugin plugin in ctx.Plugins.Plugins)
        {
            bool enabled = ctx.Plugins.IsEnabled(plugin.Name);
            Console.WriteLine($"  {plugin.DisplayName} v{plugin.Version} [{(enabled ? "已启用" : "已禁用")}]");
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
                bool enabled = ctx.Plugins.IsEnabled(name.Trim());
                ctx.Plugins.SetEnabled(name.Trim(), !enabled, Audit.Manage);
                Console.WriteLine($"[完成] {name.Trim()} 已{(enabled ? "禁用（下次启动生效）" : "启用（下次启动生效）")}。");
                break;
            }
            default:
                Console.WriteLine("[提示] 无效选项。");
                break;
        }
    }
}
