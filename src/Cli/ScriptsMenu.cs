namespace NexusPipeline.Cli;

/// <summary>脚本实例菜单兼容入口，实际操作经 Control API 执行。</summary>
internal static class ScriptsMenu
{
    public static void Show(RuntimeContext ctx)
    {
        _ = ctx;
        ControlMenu.ShowResource("script", "脚本实例");
    }
}
