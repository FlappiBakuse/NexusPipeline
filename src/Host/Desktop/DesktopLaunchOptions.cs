namespace NexusPipeline.Host.Desktop;

internal sealed record DesktopLaunchOptions(string Intent)
{
    internal static DesktopLaunchOptions Parse(string[] args)
    {
        if (args.Length == 0) return new("show");
        if (args[0].Equals("service", StringComparison.OrdinalIgnoreCase))
            return new(args.Any(value => value == "--background") ? "background" : "from-settings");
        if (args[0].Equals("restart", StringComparison.OrdinalIgnoreCase)) return new("restore");
        return new("background");
    }
    internal bool ShouldShow(bool setting, bool restoredVisible) => Intent switch { "show" => true, "from-settings" => setting, "restore" => restoredVisible, _ => false };
}
