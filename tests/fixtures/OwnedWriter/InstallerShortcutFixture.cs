using System.Text.Json;
using NexusPipeline.Platform.Storage.Installation;

internal static class InstallerShortcutFixture
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is not [var source, var root, var manager, var registry, var report]) return 2;
        await Task.Yield();
        Exception? failure = null;
        try
        {
            var expected = InstallerSnapshotCodec.Decode(File.ReadAllBytes(source), root);
            if (!InstallerMetadataInventory.Equal(expected, InstallerMetadataInventory.CaptureFrozen(expected, new(manager, registry))))
                throw new IOException("fixture_metadata_changed");
        }
        catch (Exception error) { failure = error; }
        File.WriteAllText(report, JsonSerializer.Serialize(new
        {
            accepted = failure is null, processId = Environment.ProcessId,
            imagePath = Environment.ProcessPath, imagePathLength = Environment.ProcessPath?.Length,
            apartment = Thread.CurrentThread.GetApartmentState().ToString(),
            isThreadPoolThread = Thread.CurrentThread.IsThreadPoolThread,
            errorType = failure?.GetType().Name, errorCode = failure?.HResult
        }));
        return failure is null ? 0 : 1;
    }
}
