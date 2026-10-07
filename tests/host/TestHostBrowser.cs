using System.Text.Json;

namespace NexusPipeline.Host.Composition;

internal static class TestHostBrowser
{
    private static readonly object Gate = new();

    public static bool Open(Uri address)
    {
        string? exitFile = Environment.GetEnvironmentVariable("NEXUS_TEST_HOST_EXIT_FILE");
        string? runId = Environment.GetEnvironmentVariable("NEXUS_TEST_RUN_ID");
        if (string.IsNullOrWhiteSpace(exitFile) || !Path.IsPathFullyQualified(exitFile) || string.IsNullOrWhiteSpace(runId))
            return false;
        string file = Path.GetFullPath(exitFile) + ".browser.jsonl";
        for (FileSystemInfo? entry = new FileInfo(file); entry is not null;
             entry = entry is FileInfo f ? f.Directory : ((DirectoryInfo)entry).Parent)
            if (entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked browser receipt");
        bool accepted = Environment.GetEnvironmentVariable("NEXUS_TEST_BROWSER_FAIL") != "1";
        lock (Gate) File.AppendAllText(file, JsonSerializer.Serialize(new
        {
            runId, pid = Environment.ProcessId, address = address.AbsoluteUri, accepted,
        }) + "\n");
        return accepted;
    }
}
