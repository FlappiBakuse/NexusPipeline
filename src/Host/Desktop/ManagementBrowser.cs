using NexusPipeline.Platform.Windows;

namespace NexusPipeline.Host.Desktop;

internal static class ManagementBrowser
{
    public static bool Open(Uri address)
    {
#if NEXUS_TEST_HOST
        return Composition.TestHostBrowser.Open(address);
#else
        return BrowserLauncher.Open(address);
#endif
    }
}
