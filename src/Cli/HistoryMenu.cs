namespace NexusPipeline.Cli;

/// <summary>历史记录菜单兼容入口，实际查询经 Control API 执行。</summary>
internal static class HistoryMenu
{
    public static void Show(RuntimeContext ctx)
    {
        _ = ctx;
        ControlMenu.ShowHistoryForCompatibility();
    }
}
