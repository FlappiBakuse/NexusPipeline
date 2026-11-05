using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.StorageTests;

internal static class AssetCrashFixture
{
    internal static int Run(string[] args)
    {
        if (args is not [var root, var pointValue, var report] || !Enum.TryParse<AssetPreparationPoint>(pointValue, out var target)) return 2;
        OwnedFixtureEvidence.RequirePath(root);
        OwnedFixtureEvidence.RequirePath(report);
        try
        {
            using var fixture = AssetPreparationFixture.Create(selectedRoot: root);
            fixture.Execute(new Observer(fixture, target, report));
            return 1;
        }
        catch (Exception error)
        {
            if (!File.Exists(report)) OwnedFixtureEvidence.WriteReport(report, new { status = "refused", code = error.Message, errorType = error.GetType().FullName });
            return 1;
        }
    }

    private sealed class Observer(AssetPreparationFixture fixture, AssetPreparationPoint target, string report) : IAssetPreparationObserver
    {
        public void Reached(AssetPreparationPoint point, Guid journalId, string? contentDigest)
        {
            if (point != target) return;
            OwnedFixtureEvidence.WriteReport(report, new { status = "faultReady", point = point.ToString(), journalId, contentDigest,
                processId = Environment.ProcessId, fixture.Root, fixture.Instance, fixture.Operation, plan = fixture.Prepared.Reference });
            Thread.Sleep(Timeout.Infinite);
        }
    }
}
