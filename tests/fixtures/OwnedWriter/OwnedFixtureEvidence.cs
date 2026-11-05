using System.Diagnostics;
using System.Text.Json;
using NexusPipeline.Platform.Processes;

internal static class OwnedFixtureEvidence
{
    internal static void VerifyStopped(JsonElement input, Guid instanceId, Guid journalId)
    {
        var stopped = input.GetProperty("stoppedWriter");
        if (!stopped.GetProperty("completeJobStopped").GetBoolean()) throw new IOException("fixture_job_stop_unconfirmed");
        var identity = stopped.GetProperty("identity").Deserialize<OwnedWorkerIdentity>()!;
        if (identity.InstanceId != instanceId || identity.AttemptId != journalId) throw new IOException("fixture_writer_identity_unbound");
        var family = stopped.GetProperty("family").Deserialize<ObservedMember[]>()!;
        if (family.Length is 0 or > 4096 || !family.Any(member => member.ProcessId == identity.ProcessId && member.CreatedAtUtc == identity.CreatedAtUtc) ||
            stopped.GetProperty("accounting").GetProperty("ActiveProcesses").GetUInt32() != 0)
            throw new IOException("fixture_complete_family_stop_unconfirmed");
        foreach (var member in family)
        {
            try
            {
                using var process = Process.GetProcessById(member.ProcessId);
                if (process.StartTime.ToUniversalTime() == member.CreatedAtUtc && !process.HasExited)
                    throw new IOException("fixture_old_writer_still_running");
            }
            catch (ArgumentException) { }
        }
    }

    internal static void RequirePath(string path)
    {
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new InvalidOperationException());
        var full = Path.GetFullPath(path);
        var extra = Environment.GetEnvironmentVariable("NEXUS_TEST_VOLUME_ROOT");
        if (!Path.IsPathFullyQualified(path) || !Inside(root, full) && (extra is null || !Inside(Path.GetFullPath(extra), full)))
            throw new IOException("fixture_path_outside_test_root");
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("fixture_link_rejected");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static void WriteReport(string path, object report)
    {
        RequirePath(path);
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        output.Write(JsonSerializer.SerializeToUtf8Bytes(report));
        output.Flush(flushToDisk: true);
    }

    private static bool Inside(string root, string full)
    {
        var relative = Path.GetRelativePath(root, full);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private sealed record ObservedMember(int ProcessId, DateTime CreatedAtUtc, string ImagePath, string ImageSha256);
}
