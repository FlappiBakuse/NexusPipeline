using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.WindowsTests;

[SupportedOSPlatform("windows")]
public sealed class SecretBoundaryTests
{
    private const string Canary = "notification-boundary-existing-dpapi-canary";

    [Theory(DisplayName = "Generic Secret Mutation And Standalone Preparation Cannot Publish Notification Credentials")]
    [InlineData(SecretDisposition.Keep)]
    [InlineData(SecretDisposition.Set)]
    [InlineData(SecretDisposition.Clear)]
    public async Task RequiresNotificationOwner(SecretDisposition disposition)
    {
        using var fixture = SecretTestRuntime.Create();
        using var material = new SecretValue(Canary);
        var existing = await fixture.Service.ApplyAsync(Guid.NewGuid(), fixture.Key, 0, SecretDisposition.Set, material, default);
        var key = SecretFieldKey.NotificationTask(fixture.Instance.InstanceId, Guid.NewGuid(), NotificationSecretField.WebhookUrl);
        var operation = Guid.NewGuid();
        var commits = fixture.Database.CommitSequence;
        var before = RuntimeFiles(fixture);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await fixture.Service.ApplyAsync(operation, key, 0, disposition, disposition == SecretDisposition.Set ? material : null, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await fixture.Store.ApplyAsync(operation, key, 0, disposition, disposition == SecretDisposition.Set ? material : null, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => { using var ignored = await fixture.Service.ReadAsync(key, default); });
        Assert.Throws<UnauthorizedAccessException>(() =>
            new PreparationPlanCodec(fixture.Instance, new(), fixture.Database).Freeze(
                SecretFieldStore.Identity(operation, key, 0, SecretDisposition.Set),
                Guid.NewGuid(), key, 0, material));
        Assert.Equal(commits, fixture.Database.CommitSequence);
        Assert.Equal(before, RuntimeFiles(fixture));
        Assert.Equal(existing, await fixture.Service.GetStateAsync(fixture.Key, default));
        using var original = await fixture.Service.ReadAsync(fixture.Key, default);
        original.Use(value => Assert.True(value.SequenceEqual(Canary)));
        Assert.False((await fixture.Service.GetStateAsync(key, default)).IsConfigured);
    }

    [Fact(DisplayName = "A Foreign Notification Instance Cannot Read Status")]
    public async Task RejectsForeignInstances()
    {
        using var fixture = SecretTestRuntime.Create();
        var key = SecretFieldKey.NotificationTask(Guid.NewGuid(), Guid.NewGuid(), NotificationSecretField.SmtpPassword);
        var commits = fixture.Database.CommitSequence;
        var before = RuntimeFiles(fixture);
        var error = await Assert.ThrowsAsync<IOException>(async () => await fixture.Store.GetStateAsync(key, default));
        Assert.Equal("secret_notification_instance_unbound", error.Message);
        Assert.Equal(commits, fixture.Database.CommitSequence);
        Assert.Equal(before, RuntimeFiles(fixture));
    }

    private static string[] RuntimeFiles(SecretTestRuntime fixture) =>
        Directory.EnumerateFiles(fixture.Instance.RuntimeRoot, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) != "instance.lock" &&
                !StringComparer.OrdinalIgnoreCase.Equals(path, fixture.Database.DatabasePath) &&
                !StringComparer.OrdinalIgnoreCase.Equals(path, fixture.Database.DatabasePath + "-wal") &&
                !StringComparer.OrdinalIgnoreCase.Equals(path, fixture.Database.DatabasePath + "-shm"))
            .Select(path => Path.GetRelativePath(fixture.Instance.RuntimeRoot, path) + ":" +
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).Order(StringComparer.Ordinal).ToArray();
}
