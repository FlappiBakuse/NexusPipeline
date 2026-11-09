using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class SecretStoreTests
{
    private const string Canary = "persistent-secret-fixture-canary-018";

    [Fact(DisplayName = "Accepted Secret Replay Checks Its Keyed Proof After The Original Ciphertext Has Been Retired")]
    public async Task VerifiesRetiredReplay()
    {
        var fixture = Create();
        var operation = Guid.NewGuid();
        using var value = new SecretValue(Canary);
        var first = await fixture.Store.ApplyAsync(operation, fixture.Key, 0, SecretDisposition.Set, value, default);
        var path = Assert.Single(Directory.GetFiles(Path.Combine(fixture.Instance.RuntimeRoot, "secrets/payloads")));
        await fixture.Store.ApplyAsync(Guid.NewGuid(), fixture.Key, 1, SecretDisposition.Clear, null, default);
        File.Delete(path);
        var reopened = StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Root, "diagnostics"));
        var store = new SecretFieldStore(fixture.Instance, reopened);
        Assert.Equal(first, await store.ApplyAsync(operation, fixture.Key, 0, SecretDisposition.Set, value, default));
        using var other = new SecretValue("another-fixture-value");
        await Assert.ThrowsAsync<OperationConflictException>(async () => await store.ApplyAsync(operation, fixture.Key, 0, SecretDisposition.Set, other, default));
        Assert.False((await store.GetStateAsync(fixture.Key, default)).IsConfigured);
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Instance.RuntimeRoot, "secrets/payloads")));
    }

    [Theory(DisplayName = "A Failed Replay Key Cannot Be Regenerated Or Replace The Previously Configured Secret")]
    [InlineData("missing")]
    [InlineData("corrupt")]
    public async Task PreservesReplayKeys(string fault)
    {
        var fixture = Create();
        using var value = new SecretValue(Canary);
        var first = await fixture.Store.ApplyAsync(Guid.NewGuid(), fixture.Key, 0, SecretDisposition.Set, value, default);
        var keyPath = Assert.Single(Directory.GetFiles(Path.Combine(fixture.Instance.RuntimeRoot, "secrets/keys")));
        if (fault == "missing") File.Delete(keyPath);
        else File.WriteAllBytes(keyPath, [18, 0, 4]);
        var before = CipherFiles(fixture.Instance.RuntimeRoot);
        await Assert.ThrowsAnyAsync<IOException>(async () =>
            await fixture.Store.ApplyAsync(Guid.NewGuid(), fixture.Key, 1, SecretDisposition.Set, value, default));
        Assert.Equal(first, await fixture.Store.GetStateAsync(fixture.Key, default));
        Assert.Equal(before, CipherFiles(fixture.Instance.RuntimeRoot));
        if (fault == "missing") Assert.False(File.Exists(keyPath));
        else Assert.Equal(new byte[] { 18, 0, 4 }, File.ReadAllBytes(keyPath));
    }

    [Fact(DisplayName = "Persisted Secret Survives Store And Process Restart Without Plaintext Files")]
    public async Task SurvivesProcessRestart()
    {
        var fixture = Create();
        using var value = new SecretValue(Canary);
        var accepted = await fixture.Store.ApplyAsync(Guid.NewGuid(), fixture.Key, 0, SecretDisposition.Set, value, default);
        Assert.True(accepted.IsConfigured && accepted.IsReadable);
        var reopened = StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Root, "diagnostics"));
        using var material = await new SecretFieldStore(fixture.Instance, reopened).ReadAsync(fixture.Key, default);
        material.Use(chars => Assert.True(chars.SequenceEqual(Canary)));
        var input = Path.Combine(fixture.Root, "read-input.json");
        var report = Path.Combine(fixture.Root, "read-report.json");
        var owned = Path.Combine(fixture.Root, "owned-process.json");
        File.WriteAllBytes(input, JsonSerializer.SerializeToUtf8Bytes(new { root = fixture.Instance.RuntimeRoot,
            subjectId = fixture.Key.SubjectId, instanceId = fixture.Instance.InstanceId, attemptId = Guid.NewGuid() }));
        var executable = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE") ?? throw new IOException("Missing fixture executable");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "secret-store-read-harness", input, report, owned }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(deadline.Token);
        Assert.Equal(0, process.ExitCode);
        using var observed = JsonDocument.Parse(File.ReadAllBytes(owned));
        Assert.True(observed.RootElement.GetProperty("attributionComplete").GetBoolean());
        Assert.True(observed.RootElement.GetProperty("allProcessesStopped").GetBoolean());
        var utf8 = Encoding.UTF8.GetBytes(Canary);
        var utf16 = Encoding.Unicode.GetBytes(Canary);
        foreach (var path in Directory.EnumerateFiles(fixture.Instance.RuntimeRoot, "*", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(path);
            Assert.True(bytes.AsSpan().IndexOf(utf8) < 0 && bytes.AsSpan().IndexOf(utf16) < 0);
        }
    }

    [Fact(DisplayName = "Secret Keep Clear And Replay Preserve The Original Accepted Operation")]
    public async Task PreservesAcceptedMutation()
    {
        var fixture = Create();
        var operation = Guid.NewGuid();
        using var value = new SecretValue(Canary);
        var first = await fixture.Store.ApplyAsync(operation, fixture.Key, 0, SecretDisposition.Set, value, default);
        Assert.Equal(first, await fixture.Store.ApplyAsync(operation, fixture.Key, 0, SecretDisposition.Set, value, default));
        var before = CipherFiles(fixture.Instance.RuntimeRoot);
        var kept = await fixture.Store.ApplyAsync(Guid.NewGuid(), fixture.Key, 1, SecretDisposition.Keep, null, default);
        Assert.Equal(first, kept);
        Assert.Equal(before, CipherFiles(fixture.Instance.RuntimeRoot));
        var cleared = await fixture.Store.ApplyAsync(Guid.NewGuid(), fixture.Key, 1, SecretDisposition.Clear, null, default);
        Assert.Equal(2, cleared.Revision);
        Assert.False(cleared.IsConfigured || cleared.IsReadable);
        Assert.Equal(before, CipherFiles(fixture.Instance.RuntimeRoot));
        Assert.Equal(first, await fixture.Store.ApplyAsync(operation, fixture.Key, 0, SecretDisposition.Set, value, default));
        Assert.False((await fixture.Store.GetStateAsync(fixture.Key, default)).IsConfigured);
    }

    [Fact(DisplayName = "Same Secret Operation With Different Plaintext Cannot Replay Or Overwrite")]
    public async Task RejectsChangedPlaintext()
    {
        var fixture = Create();
        var operation = Guid.NewGuid();
        using var value = new SecretValue(Canary);
        using var other = new SecretValue("different-fixture-value");
        var first = await fixture.Store.ApplyAsync(operation, fixture.Key, 0, SecretDisposition.Set, value, default);
        var before = CipherFiles(fixture.Instance.RuntimeRoot);
        await Assert.ThrowsAsync<OperationConflictException>(async () =>
            await fixture.Store.ApplyAsync(operation, fixture.Key, 0, SecretDisposition.Set, other, default));
        Assert.Equal(first, await fixture.Store.GetStateAsync(fixture.Key, default));
        Assert.Equal(before, CipherFiles(fixture.Instance.RuntimeRoot));
    }

    [Fact(DisplayName = "Corrupt Ciphertext Remains Configured Until Explicit Clear And Keep Preserves It")]
    public async Task PreservesCorruptCiphertext()
    {
        var fixture = Create();
        using var value = new SecretValue(Canary);
        var first = await fixture.Store.ApplyAsync(Guid.NewGuid(), fixture.Key, 0, SecretDisposition.Set, value, default);
        var path = Assert.Single(Directory.GetFiles(Path.Combine(fixture.Instance.RuntimeRoot, "secrets/payloads")));
        var bytes = File.ReadAllBytes(path);
        bytes[0] ^= 1;
        File.WriteAllBytes(path, bytes);
        var state = await fixture.Store.GetStateAsync(fixture.Key, default);
        Assert.True(state.IsConfigured);
        Assert.False(state.IsReadable);
        Assert.Equal(first.SecretId, state.SecretId);
        await Assert.ThrowsAsync<IOException>(async () => { using var ignored = await fixture.Store.ReadAsync(fixture.Key, default); });
        Assert.Equal(state, await fixture.Store.ApplyAsync(Guid.NewGuid(), fixture.Key, 1, SecretDisposition.Keep, null, default));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        var cleared = await fixture.Store.ApplyAsync(Guid.NewGuid(), fixture.Key, 1, SecretDisposition.Clear, null, default);
        Assert.False(cleared.IsConfigured);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact(DisplayName = "A Real Payload Creation Failure Cannot Publish A Secret Reference")]
    public async Task RejectsFailedCreation()
    {
        var fixture = Create();
        var blocked = Path.Combine(fixture.Instance.RuntimeRoot, "secrets", "payloads");
        File.WriteAllBytes(blocked, [18, 0]);
        using var value = new SecretValue(Canary);
        await Assert.ThrowsAsync<IOException>(async () =>
            await fixture.Store.ApplyAsync(Guid.NewGuid(), fixture.Key, 0, SecretDisposition.Set, value, default));
        Assert.Equal(new byte[] { 18, 0 }, File.ReadAllBytes(blocked));
        Assert.Equal(new SecretFieldStatus(0, null, false, false), await fixture.Store.GetStateAsync(fixture.Key, default));
    }

    [Fact(DisplayName = "Conflicting Revisions And Pre Cancelled Changes Do Not Change The Live Secret")]
    public async Task RejectsConflictingChanges()
    {
        var fixture = Create();
        using var value = new SecretValue(Canary);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await fixture.Store.ApplyAsync(Guid.NewGuid(), fixture.Key, 0, SecretDisposition.Set, value, cancellation.Token));
        Assert.False(Directory.Exists(Path.Combine(fixture.Instance.RuntimeRoot, "secrets", "payloads")));
        var accepted = await fixture.Store.ApplyAsync(Guid.NewGuid(), fixture.Key, 0, SecretDisposition.Set, value, default);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Store.ApplyAsync(Guid.NewGuid(), fixture.Key, 0, SecretDisposition.Clear, null, default));
        Assert.Equal(accepted, await fixture.Store.GetStateAsync(fixture.Key, default));
    }

    private sealed record Fixture(string Root, RuntimeInstance Instance, SecretFieldStore Store, SecretFieldKey Key);
    private static Fixture Create()
    {
        var parent = Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new IOException("Missing external fixture root");
        var root = Path.Combine(parent, "secret-store-" + Guid.NewGuid().ToString("N"));
        var instance = new InstanceRoot(new ContractReader()).OpenOrCreate(Path.Combine(root, "runtime"));
        return new(root, instance, new(instance, StateDatabase.CreateFresh(instance)), new("fixture-extension", Guid.NewGuid(), "credential"));
    }
    private static string[] CipherFiles(string root) => Directory.EnumerateFiles(Path.Combine(root, "secrets/payloads"))
        .Order(StringComparer.Ordinal).Select(path => Path.GetFileName(path) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
}
