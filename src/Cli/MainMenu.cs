namespace NexusPipeline.Cli;

/// <summary>命令行主菜单与状态查看（Program 的 manage/status 入口）。</summary>
internal static class MainMenu
{
    public static void Show() => ControlMenu.Show();

    public static void ShowStatus()
    {
        Ui.ClearScreen();
        CliCommandRouter.Run(new[] { "status" });
    }
}
