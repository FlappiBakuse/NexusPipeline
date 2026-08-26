namespace NexusPipeline.Cli;

/// <summary>通知渠道菜单兼容入口，实际操作经 Control API 执行。</summary>
internal static class ChannelsMenu
{
    public static void Show(RuntimeContext ctx)
    {
        _ = ctx;
        ControlMenu.ShowSettingsForCompatibility();
    }
}
