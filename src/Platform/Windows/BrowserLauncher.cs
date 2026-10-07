using System.Diagnostics;
using NexusPipeline.Shared.Logging;

namespace NexusPipeline.Platform.Windows;

internal static class BrowserLauncher
{
    public static bool Open(Uri address)
    {
        try
        {
            Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception exception)
        {
            Logger.Warn("browser_launch_failed: " + exception.GetType().Name);
            return false;
        }
    }
}
