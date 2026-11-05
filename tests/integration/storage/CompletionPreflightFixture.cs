using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class CompletionPreflightFixture : IDisposable
{
    internal ConfigurationRunFixture Run { get; } = new(additional: true);
    internal Guid SessionId { get; } = Guid.NewGuid();
    internal Guid CompletionId { get; } = Guid.NewGuid();
    internal RunAttemptPlan Parent { get; }
    internal Guid ParentId => Guid.Parse(Parent.JournalId);
    internal string Point { get; }
    private readonly OwnedCrashProcess _owner = new();
    private readonly OwnedWorkerIdentity _identity;
    private readonly string _report;

    internal CompletionPreflightFixture(string point)
    {
        Point = point;
        try
        {
            using (var root = Run.Acquire(Run.Request))
            using (var prepared = Run.Preparation.Prepare(root, Run.Owner, Run.Execution.Plan, Run.StepId, Run.Request)) Parent = Run.Parent(prepared);
            var input = Path.Combine(Run.Source.Root, "intent-owner-input.json"); _report = input + ".report";
            File.WriteAllBytes(input, JsonSerializer.SerializeToUtf8Bytes(new { runtimeRoot = Run.Source.Instance.RuntimeRoot,
                request = Run.Request, parent = Parent, sessionId = SessionId, completionOperationId = CompletionId,
                subject = ConfigurationRunFixture.Subject, point, nativeReport = Run.NativeReport }));
            Run.Lease.Dispose();
            _identity = _owner.Start(["configuration-run-recovery", input, _report], Run.Source.Root,
                Run.Source.Instance.InstanceId, SessionId, Path.Combine(Run.Source.Root, "intent-owner-process.json"));
        }
        catch { _owner.Dispose(); Run.Dispose(); throw; }
    }

    internal async Task StopOwnerAsync()
    {
        var deadline = Stopwatch.StartNew(); JsonElement report = default;
        while (deadline.Elapsed < TimeSpan.FromSeconds(45))
        {
            if (File.Exists(_report))
            {
                try { using var json = JsonDocument.Parse(File.ReadAllBytes(_report)); report = json.RootElement.Clone(); break; }
                catch (Exception error) when (error is IOException or JsonException) { }
            }
            await Task.Delay(20);
        }
        Assert.True(report.ValueKind == JsonValueKind.Object, "The real owner did not reach its intent boundary");
        Assert.True(report.GetProperty("status").GetString() == "faultReady", report.GetRawText());
        Assert.Equal(Point, report.GetProperty("point").GetString()); Assert.Equal(_identity.ProcessId, report.GetProperty("processId").GetInt32());
        Assert.Equal(SessionId, report.GetProperty("sessionId").GetGuid()); Assert.Equal(CompletionId, report.GetProperty("completionOperationId").GetGuid());
        Assert.Equal(2, report.GetProperty("physicalParentRevision").GetInt64());
        Assert.Equal(Point == "intent-ahead" ? 1 : 2, report.GetProperty("sqlParentRevision").GetInt64());
        Assert.Equal(Point == "intent-fulfilled", report.GetProperty("fulfilled").GetBoolean());
        var expected = Parent.ConfigurationPlans.Select(item => new BlobReference(item.Plan.RelativePath, item.Plan.SizeBytes, item.Plan.Sha256))
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, report.GetProperty("children").Deserialize<BlobReference[]>());
        var stopped = await _owner.StopAsync();
        Assert.True(stopped.CompleteJobStopped); Assert.Equal(0U, stopped.Accounting.ActiveProcesses);
        Assert.Contains(stopped.Family, member => member.ProcessId == _identity.ProcessId && member.CreatedAtUtc == _identity.CreatedAtUtc);
        Assert.All(stopped.Family, member => Assert.Equal(NexusPipeline.Core.Execution.WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
        File.WriteAllBytes(Path.Combine(Run.Source.Root, "intent-owner-stopped.json"), JsonSerializer.SerializeToUtf8Bytes(stopped));
        Assert.Equal(0, Run.Count("journal_writer_jobs")); Assert.Equal(0, Run.Count("journal_writer_job_roots"));
        Assert.Equal(0, Run.Count("journal_writer_job_closures")); Run.VerifyOriginal();
    }

    internal void DamageIntent(string field)
    {
        var directory = Path.Combine(Run.Source.Instance.RuntimeRoot, "journals", ParentId.ToString("D"));
        var path = Directory.GetFiles(directory).Order(StringComparer.Ordinal).Last();
        var document = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        var identity = document["participants"]![0]!["identity"]!;
        var digest = identity["evidenceDigest"]!.GetValue<string>();
        identity[field] = field == "subjectId" ? "another-valid-subject" : (digest[0] == 'c' ? "d" : "c") + digest[1..];
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(document));
    }

    internal string[] PersistentBytes()
    {
        var database = Run.Source.Database.DatabasePath;
        // SHM contains transient reader locks; only a nonempty WAL contributes persistent SQL bytes.
        var files = new[] { database, database + "-wal" }.Where(path => File.Exists(path) && (path == database || new FileInfo(path).Length != 0))
            .Concat(Directory.GetFiles(Path.Combine(Run.Source.Instance.RuntimeRoot, "journals"), "*", SearchOption.AllDirectories));
        return files.Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(Run.Source.Instance.RuntimeRoot, path) + ":" +
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
    }

    public void Dispose() { try { _owner.Dispose(); } finally { Run.Dispose(); } }
}
