using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class JournalStoreTests
{
    [Theory(DisplayName = "Every Kind Keeps Its Legal Append Only History Across New Store Instances")]
    [InlineData("configExchange")]
    [InlineData("configEdit")]
    [InlineData("fileCleanup")]
    [InlineData("runAttempt")]
    [InlineData("updateApply")]
    [InlineData("extensionApply")]
    public void KeepsAppendHistory(string kind)
    {
        var fixture = NewFixture(kind);
        var borrowedBytes = Bytes(fixture.Document); var borrowedExpected = borrowedBytes.ToArray();
        var borrowed = JournalDocument.ReadBorrowed(borrowedBytes, new());
        Assert.Equal(borrowedExpected, JournalDocument.ReadBorrowed(borrowedBytes, new()).Export());
        Array.Fill(borrowedBytes, (byte)'x');
        Assert.Throws<JsonException>(() => JournalDocument.ReadBorrowed(borrowedBytes, new()));
        Assert.Equal(borrowedExpected, borrowed.Export());
        fixture.Store.Create(Bytes(fixture.Document));
        var phases = kind is "configExchange" or "configEdit"
            ? new[] { "applying", "applied", "reverting", "reverted", "committed" }
            : ["applying", "applied", "committed"];
        foreach (var phase in phases)
        {
            Advance(fixture.Document, phase);
            fixture.Store.Append(fixture.Id, fixture.Document["revision"]!.GetValue<long>() - 1, Bytes(fixture.Document));
        }
        var reopened = new JournalStore(fixture.Instance, new ContractReader()).Read(fixture.Id);
        Assert.Equal(JournalPhase.Committed, reopened.Phase);
        Assert.Equal(phases.Length + 1, reopened.Revision);
        Assert.Equal(phases.Length + 1, Directory.GetFiles(fixture.Directory).Length);
        var previous = Directory.GetFiles(fixture.Directory).Select(File.ReadAllBytes).ToArray();
        Advance(fixture.Document, "quarantined");
        Assert.Throws<IOException>(() => fixture.Store.Append(fixture.Id, reopened.Revision, Bytes(fixture.Document)));
        Assert.Equal(previous, Directory.GetFiles(fixture.Directory).Select(File.ReadAllBytes).ToArray());
    }

    [Fact(DisplayName = "A No Effect Commit Requires Both Positive Evidence And An Explicit Receipt")]
    public void RequiresUnexecutedReceipt()
    {
        var fixture = NewFixture();
        fixture.Store.Create(Bytes(fixture.Document));
        Advance(fixture.Document, "committed", "notExecuted");
        Assert.Throws<IOException>(() => fixture.Store.Append(fixture.Id, 1, Bytes(fixture.Document)));
        Assert.Throws<IOException>(() => fixture.Store.Append(fixture.Id, 1, Bytes(fixture.Document), new(true, false, true, false)));
        var withoutReceipt = fixture.Document.DeepClone().AsObject();
        withoutReceipt["receipts"] = new JsonArray();
        Assert.Throws<IOException>(() => fixture.Store.Append(fixture.Id, 1, Bytes(withoutReceipt), new(true, false, false, false)));
        fixture.Store.Append(fixture.Id, 1, Bytes(fixture.Document), new(true, false, false, false));
        Assert.Equal(JournalPhase.Committed, fixture.Store.Read(fixture.Id).Phase);
    }

    [Theory(DisplayName = "Frozen Identity And Prior Receipts Cannot Be Changed")]
    [InlineData("operationId")]
    [InlineData("planPath")]
    [InlineData("resourceKeys")]
    [InlineData("receipt")]
    public void FreezesJournalIdentity(string field)
    {
        var fixture = NewFixture();
        fixture.Store.Create(Bytes(fixture.Document));
        Advance(fixture.Document, "applying");
        fixture.Store.Append(fixture.Id, 1, Bytes(fixture.Document));
        Advance(fixture.Document, "applied");
        switch (field)
        {
            case "operationId": fixture.Document[field] = Guid.NewGuid().ToString("D"); break;
            case "planPath": fixture.Document[field] = "blobs/other.json"; break;
            case "resourceKeys": fixture.Document[field]!.AsArray().Add("other:resource"); break;
            case "receipt": fixture.Document["receipts"]![0]!["detailCode"] = "changed"; break;
        }
        var before = Fingerprint(fixture.Directory);
        Assert.ThrowsAny<Exception>(() => fixture.Store.Append(fixture.Id, 2, Bytes(fixture.Document)));
        Assert.Equal(before, Fingerprint(fixture.Directory));
        Assert.Equal(2, fixture.Store.Read(fixture.Id).Revision);
    }

    [Theory(DisplayName = "Unrecognized History Is Rejected Without Changing Any Original Byte")]
    [InlineData("unknown-entry")]
    [InlineData("partial-revision")]
    [InlineData("gap")]
    [InlineData("unknown-phase")]
    [InlineData("illegal-edge")]
    public void PreservesUnknownHistory(string fault)
    {
        var fixture = NewFixture();
        fixture.Store.Create(Bytes(fixture.Document));
        Advance(fixture.Document, fault == "illegal-edge" ? "reverted" : "applying");
        var path = Path.Combine(fixture.Directory, fault == "gap" ? "0000000003.json" : "0000000002.json");
        if (fault == "unknown-entry") File.WriteAllText(Path.Combine(fixture.Directory, "unknown.data"), "preserve");
        else if (fault == "partial-revision") File.WriteAllText(path, "{\"format\":");
        else
        {
            if (fault == "unknown-phase") fixture.Document["phase"] = "oldPhase";
            File.WriteAllBytes(path, Bytes(fixture.Document));
        }
        var before = Fingerprint(fixture.Directory);
        Assert.ThrowsAny<Exception>(() => fixture.Store.Read(fixture.Id));
        Assert.Equal(before, Fingerprint(fixture.Directory));
    }

    [Fact(DisplayName = "Plan Bytes And Instance Identity Are Checked Before Creating Or Advancing A Journal")]
    public void ChecksPlanIdentity()
    {
        var fixture = NewFixture();
        var other = fixture.Document.DeepClone().AsObject();
        other["instanceId"] = Guid.NewGuid().ToString("D");
        Assert.Throws<IOException>(() => fixture.Store.Create(Bytes(other)));
        Assert.False(Directory.Exists(fixture.Directory));
        fixture.Store.Create(Bytes(fixture.Document));
        File.WriteAllText(fixture.Plan, "changed");
        Advance(fixture.Document, "applying");
        var before = Fingerprint(fixture.Directory);
        Assert.Throws<IOException>(() => fixture.Store.Append(fixture.Id, 1, Bytes(fixture.Document)));
        Assert.Equal(before, Fingerprint(fixture.Directory));
        Assert.Throws<IOException>(() => fixture.Store.Read(fixture.Id));
    }

    [Fact(DisplayName = "Independent Stores Cannot Both Commit The Same Journal Revision")]
    public async Task SerializesRevisionCommits()
    {
        var fixture = NewFixture();
        fixture.Store.Create(Bytes(fixture.Document));
        Assert.Throws<IOException>(() => fixture.Store.Create(Bytes(fixture.Document)));
        Advance(fixture.Document, "applying");
        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            try
            {
                new JournalStore(fixture.Instance, new ContractReader()).Append(fixture.Id, 1, Bytes(fixture.Document));
                return true;
            }
            catch (IOException) { return false; }
        })));
        Assert.Single(attempts, result => result);
        Assert.Equal(2, fixture.Store.Read(fixture.Id).Revision);
    }

    [Fact(DisplayName = "An Independent Reader Waits For The Actual Open Revision To Be Flushed And Published")]
    public async Task WaitsForPublication()
    {
        var fixture = NewFixture();
        fixture.Store.Create(Bytes(fixture.Document));
        Advance(fixture.Document, "applying");
        using var observer = new PausedPublication();
        var appender = new JournalStore(fixture.Instance, new(), observer);
        var append = Task.Run(() => appender.Append(fixture.Id, 1, Bytes(fixture.Document)));
        Task<JournalDocument>? read = null;
        try
        {
            Assert.True(observer.Created.Wait(TimeSpan.FromSeconds(10)));
            Assert.Throws<IOException>(() => File.ReadAllBytes(Path.Combine(fixture.Directory, "0000000002.json")));
            using var entered = new ManualResetEventSlim();
            read = Task.Run(() => { entered.Set(); return new JournalStore(fixture.Instance, new()).Read(fixture.Id); });
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.NotSame(read, await Task.WhenAny(read, Task.Delay(100)));
        }
        finally { observer.Release.Set(); }
        Assert.Equal(2, (await append.WaitAsync(TimeSpan.FromSeconds(10))).Revision);
        Assert.NotNull(read);
        Assert.Equal(2, (await read.WaitAsync(TimeSpan.FromSeconds(10))).Revision);
    }

    [Fact(DisplayName = "An Abandoned Publication Lock Still Rejects The Actual Incomplete Revision Without Changing Its Bytes")]
    public void RejectsAbandonedRevisions()
    {
        var fixture = NewFixture();
        fixture.Store.Create(Bytes(fixture.Document));
        var pending = Path.Combine(fixture.Directory, "0000000002.json");
        using var acquired = new ManualResetEventSlim();
        Mutex? abandoned = null;
        var thread = new Thread(() =>
        {
            abandoned = new(false, JournalPublicationGate.Name(fixture.Instance, fixture.Id),
                new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false });
            abandoned.WaitOne();
            File.WriteAllText(pending, "{\"format\":");
            acquired.Set();
        });
        thread.Start();
        Assert.True(acquired.Wait(TimeSpan.FromSeconds(10)));
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.NotNull(abandoned);
        try
        {
            var before = Fingerprint(fixture.Directory);
            Assert.Throws<JsonException>(() => new JournalStore(fixture.Instance, new()).Read(fixture.Id));
            Assert.Equal(before, Fingerprint(fixture.Directory));
        }
        finally { abandoned.Dispose(); }
    }

    [Theory(DisplayName = "Warm Successor Text Proofs Cannot Hide Changed Earlier History Or Original Plan Bytes")]
    [InlineData("identity")]
    [InlineData("receipt")]
    [InlineData("plan")]
    public void RejectsEarlierTampering(string fault)
    {
        var fixture = NewFixture();
        fixture.Store.Create(Bytes(fixture.Document));
        Advance(fixture.Document, "applying");
        fixture.Store.Append(fixture.Id, 1, Bytes(fixture.Document));
        Advance(fixture.Document, "applied");
        fixture.Store.Append(fixture.Id, 2, Bytes(fixture.Document));
        Assert.Equal(3, new JournalStore(fixture.Instance, new()).Read(fixture.Id).Revision);
        Assert.Equal(3, new JournalStore(fixture.Instance, new()).Read(fixture.Id).Revision);
        var latestPath = Path.Combine(fixture.Directory, "0000000003.json");
        var originalLatest = File.ReadAllBytes(latestPath);
        if (fault == "plan") File.WriteAllText(fixture.Plan, "{\"fixture\":null}");
        else
        {
            var earlierPath = Path.Combine(fixture.Directory, "0000000002.json");
            var earlier = JsonNode.Parse(File.ReadAllBytes(earlierPath))!.AsObject();
            if (fault == "identity") earlier["operationId"] = Guid.NewGuid().ToString("D");
            else earlier["receipts"]![0]!["detailCode"] = "different_observation";
            File.WriteAllBytes(earlierPath, Bytes(earlier));
        }
        var history = Fingerprint(fixture.Directory);
        var originalPlan = File.ReadAllBytes(fixture.Plan);
        Assert.Equal(originalLatest, File.ReadAllBytes(latestPath));
        Assert.Throws<IOException>(() => new JournalStore(fixture.Instance, new()).Read(fixture.Id));
        Assert.Equal(history, Fingerprint(fixture.Directory));
        Assert.Equal(originalPlan, File.ReadAllBytes(fixture.Plan));
        Assert.Equal(originalLatest, File.ReadAllBytes(latestPath));
    }

    [Theory(DisplayName = "An Explicit Protected Journal Scope Prevents Replacing Prior Inputs And Still Rejects New Entries And Cold Tampering")]
    [InlineData("history")]
    [InlineData("plan")]
    [InlineData("unknown-entry")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void ProtectsJournalInputs(string fault)
    {
        var fixture = NewFixture();
        fixture.Store.Create(Bytes(fixture.Document));
        Advance(fixture.Document, "applying");
        fixture.Store.Append(fixture.Id, 1, Bytes(fixture.Document));
        using var held = fixture.Store.ProtectReadHistory(fixture.Id);
        Advance(fixture.Document, "applied");
        fixture.Store.Append(fixture.Id, 2, Bytes(fixture.Document));
        Assert.Equal(3, fixture.Store.Read(fixture.Id).Revision);
        Assert.Equal(3, fixture.Store.ReadAllRevisions(fixture.Id).Count);
        if (fault == "unknown-entry")
        {
            File.WriteAllText(Path.Combine(fixture.Directory, "unknown.bin"), "preserve");
            var before = Fingerprint(fixture.Directory);
            Assert.Throws<IOException>(() => fixture.Store.Read(fixture.Id));
            Advance(fixture.Document, "reverting");
            Assert.Throws<IOException>(() => fixture.Store.Append(fixture.Id, 3, Bytes(fixture.Document)));
            Assert.Equal(before, Fingerprint(fixture.Directory));
            held.Dispose();
            Assert.Throws<IOException>(() => new JournalStore(fixture.Instance, new()).Read(fixture.Id));
            Assert.Equal(before, Fingerprint(fixture.Directory));
            return;
        }
        var path = fault == "plan" ? fixture.Plan : Path.Combine(fixture.Directory, "0000000001.json");
        var original = File.ReadAllBytes(path);
        Assert.Throws<IOException>(() => File.WriteAllText(path, "changed"));
        Assert.Throws<IOException>(() => File.Move(path, path + ".replaced"));
        Assert.Throws<IOException>(() => File.Delete(path));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(3, fixture.Store.Read(fixture.Id).Revision);
        held.Dispose();
        File.WriteAllText(path, "changed");
        var changed = File.ReadAllBytes(path);
        Assert.ThrowsAny<Exception>(() => new JournalStore(fixture.Instance, new()).Read(fixture.Id));
        Assert.Equal(changed, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void FallsBackSafely(bool byteLimit)
    {
        var fixture = NewFixture();
        if (byteLimit)
        {
            var resources = new JsonArray();
            for (var index = 0; index < 1500; index++) resources.Add($"fixture:{index:D4}:" + new string('x', 180));
            fixture.Document["resourceKeys"] = resources;
        }
        var current = fixture.Store.Create(Bytes(fixture.Document));
        using var held = fixture.Store.ProtectReadHistory(fixture.Id);
        long total = current.Export().Length;
        var crossed = false;
        while (!crossed)
        {
            Advance(fixture.Document, "applying");
            var next = Bytes(fixture.Document);
            crossed = byteLimit ? total + next.Length > 16777216 : current.Revision == 256;
            if (!crossed)
                Assert.Throws<IOException>(() => File.Open(Path.Combine(fixture.Directory, "0000000001.json"),
                    FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose());
            current = fixture.Store.Append(fixture.Id, current.Revision, next);
            total += next.Length;
            Assert.True(current.Revision <= 257);
        }
        var first = Path.Combine(fixture.Directory, "0000000001.json");
        using (File.Open(first, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) { }
        using var readScope = JournalReadScope.Hold(fixture.Store, fixture.Id);
        Assert.Equal(current.Revision, fixture.Store.Read(fixture.Id).Revision);
        File.WriteAllText(first, "changed");
        var before = Fingerprint(fixture.Directory);
        Assert.ThrowsAny<Exception>(() => fixture.Store.Read(fixture.Id));
        Advance(fixture.Document, "applying");
        Assert.ThrowsAny<Exception>(() => fixture.Store.Append(fixture.Id, current.Revision, Bytes(fixture.Document)));
        Assert.Equal(before, Fingerprint(fixture.Directory));
        Console.WriteLine($"protection fallback: bytes={total}; revisions={current.Revision}; limit={(byteLimit ? "bytes" : "revisions")}");
    }

    private sealed class PausedPublication : IJournalPublicationObserver, IDisposable
    {
        internal ManualResetEventSlim Created { get; } = new();
        internal ManualResetEventSlim Release { get; } = new();
        public void RevisionCreated(string path)
        {
            Created.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Journal publication fixture was not released");
        }
        public void Dispose() { Created.Dispose(); Release.Dispose(); }
    }

    private static Fixture NewFixture(string kind = "configExchange")
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new InvalidOperationException(), Guid.NewGuid().ToString("N"));
        var contracts = new ContractReader();
        var instance = new InstanceRoot(contracts).OpenOrCreate(root);
        var id = Guid.NewGuid();
        byte[] plan = "{\"fixture\":true}"u8.ToArray();
        var digest = Convert.ToHexStringLower(SHA256.HashData(plan));
        var planPath = $"blobs/plans/{digest}.json";
        var fullPlan = Path.Combine(root, planPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPlan)!);
        File.WriteAllBytes(fullPlan, plan);
        var document = new JsonObject
        {
            ["format"] = "nexus.execution.journal", ["version"] = 1,
            ["instanceId"] = instance.InstanceId.ToString("D"), ["journalId"] = id.ToString("D"),
            ["operationId"] = Guid.NewGuid().ToString("D"), ["operationKind"] = kind,
            ["phase"] = "prepared", ["revision"] = 1L, ["updatedAt"] = "2026-10-19T00:00:00Z",
            ["planPath"] = planPath, ["planDigest"] = digest, ["resourceKeys"] = new JsonArray("fixture:configuration"),
            ["participants"] = new JsonArray(), ["receipts"] = new JsonArray()
        };
        if (kind == "runAttempt")
        {
            document["runId"] = Guid.NewGuid().ToString("D");
            document["attemptId"] = Guid.NewGuid().ToString("D");
        }
        return new(instance, new JournalStore(instance, contracts), id, document, Path.Combine(root, "journals", id.ToString("D")), fullPlan);
    }

    private static void Advance(JsonObject document, string phase, string effect = "completed")
    {
        document["revision"] = document["revision"]!.GetValue<long>() + 1;
        document["phase"] = phase;
        document["receipts"]!.AsArray().Add(new JsonObject
        {
            ["receiptId"] = Guid.NewGuid().ToString("D"), ["recordedAt"] = "2026-10-19T00:00:00Z",
            ["stepId"] = "fixture-step", ["effect"] = effect, ["detailCode"] = "fixture_observed"
        });
    }

    private static byte[] Bytes(JsonObject document) => JsonSerializer.SerializeToUtf8Bytes(document);
    private static string[] Fingerprint(string directory) => Directory.GetFiles(directory).Order(StringComparer.Ordinal)
        .Select(path => Path.GetFileName(path) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
    private sealed record Fixture(RuntimeInstance Instance, JournalStore Store, Guid Id, JsonObject Document, string Directory, string Plan);
}
