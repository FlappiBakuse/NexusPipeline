using System.Runtime.Versioning;
using NexusPipeline.Platform.Processes;

[SupportedOSPlatform("windows")]
internal static class PreviewOwnerFixture
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is not [var report, var name]) return 2;
        OwnedFixtureEvidence.RequirePath(report);
        using var domain = new OwnedPreviewJob(name);
        using var helper = new OwnedWorkerJob(); helper.ObserveProcessIdentities(_ => { });
        using var output = new OwnedProcessOutput();
        var executable = Environment.ProcessPath ?? throw new IOException("fixture_self_image_missing");
        var child = helper.StartPreview(domain, executable, [report + ".child", "preview-owned-child"],
            Path.GetDirectoryName(report)!, Guid.NewGuid(), Guid.NewGuid(), _ => { }, output);
        OwnedFixtureEvidence.WriteReport(report + ".prepared", new { jobName = name,
            creator = ProcessIdentityReader.Read(Environment.ProcessId), child = new ProcessIdentity(child.ProcessId,
                child.CreatedAtUtc, child.ImagePath, child.ImageSha256, child.UserSid, child.IsElevated) });
        File.Move(report + ".prepared", report);
        await Task.Delay(Timeout.InfiniteTimeSpan); GC.KeepAlive(domain); GC.KeepAlive(helper); return 0;
    }
}
