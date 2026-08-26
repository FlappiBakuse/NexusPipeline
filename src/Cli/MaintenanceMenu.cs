namespace NexusPipeline.Cli;

/// <summary>维护菜单兼容入口，实际操作经 Control API 执行。</summary>
internal static class MaintenanceMenu
{
    public static void Show(RuntimeContext ctx)
    {
        _ = ctx;
        ControlMenu.ShowMaintenanceForCompatibility();
    }
}
