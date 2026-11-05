using System.Text.Json;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Sqlite;

internal static class SecretStoreFixture
{
    internal static async Task<int> HarnessAsync(string[] args)
    {
        if (args is not [var input, var report, var receipt]) return 2;
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new IOException("Missing external root"));
        var working = Path.GetDirectoryName(Path.GetFullPath(input))!;
        if (!working.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || Path.GetDirectoryName(Path.GetFullPath(report)) != working || Path.GetDirectoryName(Path.GetFullPath(receipt)) != working)
            throw new IOException("Secret fixture escaped owned root");
        using var settings = JsonDocument.Parse(File.ReadAllBytes(input));
        return await OwnedFixtureProcess.RunAsync(Environment.ProcessPath!, ["secret-store-read", input, report], working, report, receipt,
            TimeSpan.FromSeconds(20), settings.RootElement.GetProperty("instanceId").GetGuid(), settings.RootElement.GetProperty("attemptId").GetGuid());
    }

    internal static async Task<int> ReadAsync(string[] args)
    {
        if (args is not [var input, var report]) return 2;
        using var settings = JsonDocument.Parse(File.ReadAllBytes(input));
        var root = Path.GetFullPath(settings.RootElement.GetProperty("root").GetString()!);
        OwnedFixtureEvidence.RequirePath(root);
        var allowed = Path.GetFullPath(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new IOException("Missing external root"));
        if (!root.StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Secret reader escaped owned root");
        var instance = new InstanceRoot(new ContractReader()).OpenExisting(root);
        if (instance.InstanceId != settings.RootElement.GetProperty("instanceId").GetGuid()) throw new IOException("Secret fixture instance changed");
        var database = StateDatabase.OpenExisting(instance, Path.Combine(Path.GetDirectoryName(input)!, "reader-diagnostics"));
        var store = new SecretFieldStore(instance, database);
        using var material = await store.ReadAsync(new("fixture-extension", settings.RootElement.GetProperty("subjectId").GetGuid(), "credential"), default);
        var matches = false;
        material.Use(chars => matches = chars.SequenceEqual("persistent-secret-fixture-canary-018"));
        if (!matches) throw new IOException("Persisted secret fixture did not round trip");
        File.WriteAllBytes(report, JsonSerializer.SerializeToUtf8Bytes(new { status = "PASS", processId = Environment.ProcessId,
            scope = "Fresh process reads actual persisted current-user DPAPI payload; fixture-only canary", instanceId = instance.InstanceId }));
        return 0;
    }
}
