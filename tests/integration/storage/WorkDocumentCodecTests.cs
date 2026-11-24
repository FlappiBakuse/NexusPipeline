using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Scheduling;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Scheduling;
using Xunit;
using Xunit.Abstractions;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class WorkDocumentCodecTests(ITestOutputHelper output)
{
    [Fact(DisplayName = "A Verified Scheduled Occurrence Only Encodes Its Exact Candidate From A Unique Task Plan")]
    public async Task EncodesOnlyMatchingTask()
    {
        await using var fixture = new NotificationScheduledFixture { HoldCallback = true, SubmitNotification = false };
        await fixture.Activate();
        await fixture.Tick.TickAsync();
        await fixture.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var pending = Assert.Single(fixture.States());
        Assert.Null(pending.Result);
        var document = pending.Source.Document;
        fixture.CallbackGate.TrySetResult();
        await fixture.PumpUntilResult();
        var codec = new ScheduledProviderCodec(fixture.Base.Source.Instance, new());
        var decoys = Enumerable.Range(0, 256).Select(_ => document.Task with { TaskId = Guid.NewGuid() }).ToArray();
        ScheduledProviderDocument WithPlans(ScheduledWorkPlans plans) => document with { Plans = codec.Persist(plans) };
        var valid = new ScheduledWorkPlans(document.PlansRevision, [.. decoys.Take(255), document.Task]);
        var solo = Measure(WithPlans(new(document.PlansRevision, [document.Task])), 1, 100);
        var populated = Measure(WithPlans(valid), 256, 40);
        var missing = WithPlans(new(document.PlansRevision, [.. decoys]));
        var absent = Profile(() => Assert.Throws<IOException>(() => codec.Require(missing)));
        output.WriteLine($"work-plan missing tasks=256 encodes={absent}");
        var changed = WithPlans(valid with { Tasks = valid.Tasks.SetItem(255, document.Task with { TimeoutSeconds = document.Task.TimeoutSeconds + 1 }) });
        Assert.Equal("scheduled_provider_source_unbound", Assert.Throws<IOException>(() => codec.Require(changed)).Message);
        var duplicate = WithPlans(valid with { Tasks = valid.Tasks.Add(document.Task) });
        Assert.Equal("scheduled_work_plans_invalid", Assert.Throws<ArgumentException>(() => codec.Require(duplicate)).Message);
        var revised = WithPlans(valid with { Revision = document.PlansRevision + 1 });
        Assert.Equal("scheduled_provider_source_unbound", Assert.Throws<IOException>(() => codec.Require(revised)).Message);
        Assert.Equal(2, solo);
        Assert.Equal(2, populated);
        Assert.Equal(0, absent);

        long Measure(ScheduledProviderDocument supplied, int tasks, int iterations)
        {
            var encodes = Profile(() => codec.Require(supplied));
            for (var i = 0; i < 5; i++) codec.Require(supplied);
            using var process = Process.GetCurrentProcess();
            var cpuBefore = process.TotalProcessorTime;
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            for (var i = 0; i < iterations; i++) codec.Require(supplied);
            var elapsed = Stopwatch.GetElapsedTime(started);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            var cpu = process.TotalProcessorTime - cpuBefore;
            output.WriteLine($"work-plan tasks={tasks} bytes={supplied.Plans.SizeBytes} encodes={encodes} repetitions={iterations} elapsed-ms={elapsed.TotalMilliseconds:F3} cpu-ms={cpu.TotalMilliseconds:F3} allocated-bytes={allocated}");
            return encodes;
        }
    }

    [Fact(DisplayName = "Scheduled And Manual Blobs Still Require The Strict Json Shape On Read")]
    public void RetainsStoredDocumentShape()
    {
        using var fixture = new ExecutionAdmissionFixture();
        var instance = fixture.Source.Instance;
        var codec = new ScheduledProviderCodec(instance, new());
        var manual = new ManualWorkCodec(instance, new());
        var plans = new ScheduledWorkPlans(1, [new(Guid.NewGuid(), 1, true, "UTC", [1, 2], 600, 60, [])]);
        var reference = codec.Persist(plans);
        var begin = new ManualWorkBegin("nexus.manual.work-begin", 1, reference, DateTimeOffset.UtcNow, 7);
        Assert.Equal(begin, manual.ReadDocument<ManualWorkBegin>(manual.Persist(begin)));
        var json = JsonNode.Parse(ScheduledProviderCodec.Encode(plans))!.AsObject();
        Assert.Equal(plans.Tasks[0].TaskId, Assert.Single(codec.ReadDocument<ScheduledWorkPlans>(reference).Tasks).TaskId);
        var spaced = Seed(" \r\n" + json.ToJsonString() + " \r\n");
        Assert.Equal(plans.Tasks[0].TaskId, Assert.Single(codec.ReadDocument<ScheduledWorkPlans>(spaced).Tasks).TaskId);
        json["unknown"] = true;
        Assert.Throws<JsonException>(() => codec.ReadDocument<ScheduledWorkPlans>(Seed(json.ToJsonString())));
        json.Remove("unknown");
        json["tasks"]!.AsArray()[0]!.AsObject().Remove("ownerTaskId");
        Assert.Equal("scheduled_provider_source_unbound",
            Assert.Throws<IOException>(() => codec.ReadDocument<ScheduledWorkPlans>(Seed(json.ToJsonString()))).Message);
        Assert.Equal("duplicate_json_member", Assert.Throws<JsonException>(() =>
            manual.ReadDocument<ManualWorkBegin>(Seed("{\"nested\":{\"a\":1,\"\\u0061\":2}}"))).Message);
        Assert.Equal("invalid_json", Assert.Throws<JsonException>(() =>
            manual.ReadDocument<ManualWorkBegin>(Seed(new string('[', 65) + "0" + new string(']', 65)))).Message);
        Assert.Equal("json_string_limit", Assert.Throws<JsonException>(() =>
            manual.ReadDocument<ManualWorkBegin>(Seed("\"" + new string('a', StrictJson.MaximumStringBytes + 1) + "\""))).Message);
        Assert.Equal("scheduled_provider_source_unbound", Assert.Throws<IOException>(() =>
            codec.ReadDocument<ScheduledWorkPlans>(reference with { SizeBytes = StrictJson.MaximumBytes + 1 })).Message);

        BlobReference Seed(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var relativePath = $"blobs/plans/{digest}.json";
            File.WriteAllBytes(Path.Combine(instance.RuntimeRoot, relativePath), bytes);
            return new(relativePath, bytes.Length, digest);
        }
    }

    private static long Profile(Action action)
    {
        var previous = Console.Error;
        using var capture = new StringWriter();
        try
        {
            Console.SetError(capture);
            using var profile = StartupProfile.Begin("work-plan-encoding-test");
            action();
        }
        finally { Console.SetError(previous); }
        using var report = JsonDocument.Parse(capture.ToString());
        foreach (var step in report.RootElement.GetProperty("steps").EnumerateArray())
            if (step.GetProperty("name").GetString() == "schedule.codec.encode") return step.GetProperty("count").GetInt64();
        return 0;
    }
}
