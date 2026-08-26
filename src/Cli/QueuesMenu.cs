namespace NexusPipeline.Cli;

/// <summary>调度队列菜单兼容入口，实际操作经 Control API 执行。</summary>
internal static class QueuesMenu
{
    public static void Show(RuntimeContext ctx)
    {
        _ = ctx;
        ControlMenu.ShowResource("queue", "调度队列");
    }
}
