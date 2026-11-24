using System.Diagnostics;
using NexusPipeline.Platform.Media;

internal static class PreviewCaptureGate
{
    internal static async Task<int> RunAsync()
    {
        var gate = AppContext.BaseDirectory;
        OwnedFixtureEvidence.RequirePath(gate);
        for (var ordinal = 1; ordinal <= 2; ordinal++)
        {
            var arm = Path.Combine(gate, $"capture-{ordinal}.armed");
            var ready = Path.Combine(gate, $"capture-{ordinal}.ready.json");
            if (!File.Exists(arm) || File.Exists(ready)) continue;
            using var self = Process.GetCurrentProcess();
            OwnedFixtureEvidence.WriteReport(ready, new { ordinal, processId = self.Id, createdAtUtc = self.StartTime.ToUniversalTime() });
            var release = Path.Combine(gate, $"capture-{ordinal}.release");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            while (!File.Exists(release)) await Task.Delay(5, timeout.Token);
            break;
        }
        return await WindowCaptureWorker.RunAsync();
    }
}
