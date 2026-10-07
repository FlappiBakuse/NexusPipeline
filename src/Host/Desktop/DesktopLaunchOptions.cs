namespace NexusPipeline.Host.Desktop;

internal sealed record DesktopLaunchOptions(string Intent, bool IsRestart = false)
{
    internal static DesktopLaunchOptions Parse(string[] args)
    {
        if (args.Length == 0) return new("show");
        if (args[0].Equals("service", StringComparison.OrdinalIgnoreCase))
            return new(args.Any(value => value == "--background") ? "background" : "from-settings");
        if (args[0].Equals("restart", StringComparison.OrdinalIgnoreCase)) return new("restore", IsRestart: true);
        return new("background");
    }
    internal bool ShouldShow(bool lightweight, bool setting, bool restoredVisible) => lightweight
        ? !IsRestart && (Intent is "show" or "from-settings") && setting
        : Intent switch { "show" => true, "from-settings" => setting, "restore" => restoredVisible, _ => false };
}
