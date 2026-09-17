using NexusPipeline.Models;
using NexusPipeline.Plugins;
using NexusPipeline.Services.Networking;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class PluginRepositoryOperationsTests
{
    [Fact]
    public async Task InstallAsync_DoesNotTreatVerifiedOwnershipAsPendingTransaction()
    {
        PluginCatalogEntry entry = new(
            "bettergi",
            "BetterGI",
            "原神",
            "测试插件",
            "0.2.0",
            "data-specialized",
            "",
            Array.Empty<string>(),
            "0.0.0",
            "https://raw.githubusercontent.com/FlappiBakuse/NexusPipeline-Plugins/main/packages/BetterGI/BetterGI-0.2.0.zip",
            new string('a', 64),
            1)
        {
            ArtifactName = "BetterGI",
        };
        var ownership = new Dictionary<string, PluginOwnership>(StringComparer.OrdinalIgnoreCase)
        {
            [entry.Name] = new PluginOwnership
            {
                Name = entry.Name,
                ArtifactName = entry.ArtifactName,
                Version = "0.1.0",
            },
        };
        var staged = new PluginPendingOperation
        {
            Action = "install",
            Name = entry.Name,
            ArtifactName = entry.ArtifactName,
            Version = entry.Version,
            Phase = "pending",
        };
        string? stagedAction = null;
        var operations = new PluginRepositoryOperations(
            () => Array.Empty<PluginSummary>(),
            new PluginPackageService(new OutboundHttpClientProvider(() => new AppSettings())),
            (_, _) => Task.FromResult(entry),
            () => { },
            ownership: () => ownership,
            hasPending: _ => false,
            stage: (_, action, _) =>
            {
                stagedAction = action;
                return Task.FromResult(staged);
            });

        PluginPendingOperation result = await operations.InstallAsync(entry.Name, update: false);

        Assert.Equal("install", stagedAction);
        Assert.Same(staged, result);
    }
}
