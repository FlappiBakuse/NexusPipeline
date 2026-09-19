using System.Text.Json;
using Xunit;
using NexusPipeline.ControlPlane.Http;
using NexusPipeline.Host.Lifecycle;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.Plugins.Repository;
using NexusPipeline.Modules.Scheduling.Contracts;
using NexusPipeline.Modules.Settings;
using NexusPipeline.Modules.Updates;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Shared.Serialization;

namespace NexusPipeline.Tests.Updates;

public sealed class PluginAutoUpdateServiceTests
{
    [Fact]
    public void StartupUpdateStagesInstalledDisabledManualPluginBeforeRecoveryAndLoad()
    {
        string root = NewTempDir();
        try
        {
            string plugins = Path.Combine(root, "plugins");
            string state = Path.Combine(root, "state");
            string stagingRoot = Path.Combine(state, "staging");
            string pendingPath = Path.Combine(state, "pending.json");
            string ownershipPath = Path.Combine(state, "ownership.json");
            string backupRoot = Path.Combine(state, "backup");
            string local = Path.Combine(plugins, "LocalHelper");
            Directory.CreateDirectory(local);
            File.WriteAllText(Path.Combine(local, "plugin.json"), "{\"version\":\"0.1.0\"}");
            WriteOwnership(ownershipPath, new PluginOwnership
            {
                Name = "local-helper",
                ArtifactName = "LocalHelper",
                Version = "0.1.0",
                Kind = "managed-code",
                ApiVersion = "1.7",
            });

            PluginStoreItem candidate = Candidate();
            var repository = new FakeRepository(candidate);
            string stagedPath = Path.Combine(stagingRoot, "LocalHelper.update");
            Directory.CreateDirectory(stagedPath);
            File.WriteAllText(Path.Combine(stagedPath, "plugin.json"), "{\"version\":\"0.2.0\"}");
            PluginPendingOperation operation = Pending(stagedPath);
            repository.Stage = (_, _) =>
            {
                PluginInstallRecovery.AddPending(operation, pendingPath);
                repository.Pending = new[] { operation };
                return Task.FromResult(new PluginBatchUpdateResult(
                    new[] { candidate },
                    new[] { operation },
                    Array.Empty<PluginBatchUpdateFailure>(),
                    Canceled: false));
            };

            var settings = new AppSettings { PluginAutoUpdateEnabled = true };
            settings.PluginPreferences["local-helper"] = new PluginPreference { Enabled = false };
            var service = new PluginAutoUpdateService(
                () => settings,
                repository,
                _ => AutoUpdateIdleAttempt.Blocked(new AutoUpdateIdleBlocker("host_busy", "busy", null, null)),
                _ => new RestartRequestResult(false, "not_expected", "startup staging is applied before load"));

            service.RunStartupBeforePlugins();

            Assert.Equal(1, repository.CandidateReads);
            Assert.Equal(new[] { "local-helper" }, repository.StagedCandidates.Select(item => item.Name));
            Assert.False(settings.PluginPreferences["local-helper"].Enabled);
            Assert.Single(PluginInstallRecovery.ReadPending(pendingPath));

            // Bootstrap applies the staged journal before LoadAll; the installed files are ready first.
            Assert.True(PluginInstallRecovery.ApplyPending(
                plugins,
                pendingPath,
                ownershipPath,
                stagingRoot,
                backupRoot));
            Assert.Contains("0.2.0", File.ReadAllText(Path.Combine(plugins, "LocalHelper", "plugin.json")));
            Assert.Empty(PluginInstallRecovery.ReadPending(pendingPath));
            Assert.False(settings.PluginPreferences["local-helper"].Enabled);
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    [Fact]
    public void StartupUpdateSwitchOffSkipsCatalogAndDownload()
    {
        var repository = new FakeRepository(Candidate());
        var settings = new AppSettings { PluginAutoUpdateEnabled = false };
        var service = new PluginAutoUpdateService(
            () => settings,
            repository,
            _ => throw new Xunit.Sdk.XunitException("disabled plugin automation must not acquire a lease"),
            _ => throw new Xunit.Sdk.XunitException("disabled plugin automation must not request restart"));

        service.RunStartupBeforePlugins();

        Assert.Equal(0, repository.CandidateReads);
        Assert.Empty(repository.StagedCandidates);
    }

    [Fact]
    public async Task RuntimeStagingWaitsForMaintenanceLeaseBeforeRestart()
    {
        PluginStoreItem candidate = Candidate();
        var repository = new FakeRepository(candidate);
        PluginPendingOperation operation = Pending("C:/test/staging/LocalHelper.update");
        repository.Stage = (_, _) =>
        {
            repository.Pending = new[] { operation };
            return Task.FromResult(new PluginBatchUpdateResult(
                new[] { candidate },
                new[] { operation },
                Array.Empty<PluginBatchUpdateFailure>(),
                Canceled: false));
        };

        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var restart = new TaskCompletionSource<HostMaintenanceLease>(TaskCreationOptions.RunContinuationsAsynchronously);
        var settings = new AppSettings { PluginAutoUpdateEnabled = true };
        int idleChecks = 0;
        var service = new PluginAutoUpdateService(
            () => settings,
            repository,
            horizon =>
            {
                Assert.Equal(PluginAutoUpdateService.IdleHorizon, horizon);
                Interlocked.Increment(ref idleChecks);
                return AutoUpdateIdleAttempt.Accepted(new HostMaintenanceLease());
            },
            lease =>
            {
                restart.TrySetResult(lease);
                return new RestartRequestResult(true, "accepted", "restart accepted");
            },
            () => now,
            async (duration, token) =>
            {
                await Task.Yield();
                token.ThrowIfCancellationRequested();
                now = now.Add(duration);
            });

        service.Start();
        try
        {
            HostMaintenanceLease lease = await restart.Task.WaitAsync(TimeSpan.FromSeconds(5));
            lease.Dispose();
        }
        finally
        {
            service.Stop();
        }

        Assert.Equal(1, repository.CandidateReads);
        Assert.Single(repository.StagedCandidates);
        Assert.True(idleChecks > 0);
    }

    [Fact]
    public async Task RuntimePendingRemovedBeforeMaintenanceLease_DoesNotRestart()
    {
        PluginStoreItem candidate = Candidate();
        var repository = new FakeRepository(candidate);
        PluginPendingOperation operation = Pending("C:/test/staging/LocalHelper.update");
        repository.Stage = (_, _) => Task.FromResult(new PluginBatchUpdateResult(
            new[] { candidate },
            new[] { operation },
            Array.Empty<PluginBatchUpdateFailure>(),
            Canceled: false));
        var pendingRechecked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        repository.PendingReader = read =>
        {
            if (read == 1) return new[] { operation };
            pendingRechecked.TrySetResult(true);
            return Array.Empty<PluginPendingOperation>();
        };

        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var settings = new AppSettings { PluginAutoUpdateEnabled = true };
        int idleChecks = 0;
        int restarts = 0;
        var service = new PluginAutoUpdateService(
            () => settings,
            repository,
            _ =>
            {
                Interlocked.Increment(ref idleChecks);
                return AutoUpdateIdleAttempt.Accepted(new HostMaintenanceLease());
            },
            _ =>
            {
                Interlocked.Increment(ref restarts);
                return new RestartRequestResult(true, "accepted", "restart accepted");
            },
            () => now,
            async (duration, token) =>
            {
                if (duration <= PluginAutoUpdateService.InitialCheckDelay)
                {
                    await Task.Yield();
                    token.ThrowIfCancellationRequested();
                    now = now.Add(duration);
                    return;
                }
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            });

        service.Start();
        try
        {
            await pendingRechecked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            service.Stop();
        }

        Assert.Equal(1, repository.CandidateReads);
        Assert.Single(repository.StagedCandidates);
        Assert.True(repository.PendingReads >= 2);
        Assert.Equal(0, idleChecks);
        Assert.Equal(0, restarts);
    }

    private static PluginStoreItem Candidate() => new(
        "local-helper",
        "LocalHelper",
        "Local Helper",
        "",
        "fixture",
        "0.2.0",
        "managed-code",
        "1.7",
        Array.Empty<string>(),
        "0.16.0",
        Installed: true,
        InstalledVersion: "0.1.0",
        UpdateAvailable: true,
        Compatible: true,
        CompatibilityReason: "",
        ManagedByStore: false,
        PendingAction: "",
        PendingVersion: "",
        Status: "update-available",
        InstalledName: "local-helper",
        Changelog: Array.Empty<PluginChangelogEntry>());

    private static PluginPendingOperation Pending(string stagedPath) => new()
    {
        Action = "update",
        Name = "local-helper",
        ArtifactName = "LocalHelper",
        Version = "0.2.0",
        Kind = "managed-code",
        ApiVersion = "1.7",
        Sha256 = new string('a', 64),
        StagedPath = stagedPath,
        Phase = "pending",
    };

    private static string NewTempDir()
    {
        string path = Path.Combine(Path.GetTempPath(), "np-plugin-auto-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDir(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private static void WriteOwnership(string path, params PluginOwnership[] owners)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        JsonUtil.WriteAtomic(
            path,
            JsonSerializer.Serialize(new PluginOwnershipState
            {
                SchemaVersion = 2,
                Plugins = owners.ToList(),
            },
            JsonOpts.Indented));
    }

    private sealed class FakeRepository(PluginStoreItem candidate) : IPluginAutoUpdateRepository
    {
        internal int CandidateReads { get; private set; }
        internal int PendingReads { get; private set; }
        internal List<PluginStoreItem> StagedCandidates { get; } = new();
        internal IReadOnlyList<PluginPendingOperation> Pending { get; set; } = Array.Empty<PluginPendingOperation>();
        internal Func<IReadOnlyList<PluginStoreItem>, CancellationToken, Task<PluginBatchUpdateResult>>? Stage { get; set; }
        internal Func<int, IReadOnlyList<PluginPendingOperation>>? PendingReader { get; set; }

        public Task<IReadOnlyList<PluginStoreItem>> GetUpdateCandidatesAsync(CancellationToken cancellationToken)
        {
            CandidateReads++;
            return Task.FromResult<IReadOnlyList<PluginStoreItem>>(new[] { candidate });
        }

        public Task<PluginBatchUpdateResult> StageUpdatesAsync(
            IReadOnlyList<PluginStoreItem> candidates,
            CancellationToken cancellationToken)
        {
            StagedCandidates.AddRange(candidates);
            return Stage?.Invoke(candidates, cancellationToken)
                ?? Task.FromResult(new PluginBatchUpdateResult(
                    candidates,
                    Array.Empty<PluginPendingOperation>(),
                    Array.Empty<PluginBatchUpdateFailure>(),
                    Canceled: false));
        }

        public IReadOnlyList<PluginPendingOperation> ReadPendingOperations()
        {
            int read = ++PendingReads;
            return PendingReader?.Invoke(read) ?? Pending;
        }
    }
}
