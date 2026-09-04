using NexusPipeline.App.Queries;
using Xunit;

namespace NexusPipeline.Tests;

/// <summary>v0.13.5 核心架构边界：防止运行时所有权治理在后续修改中回退。</summary>
public sealed class ArchitectureBoundaryTests
{
    [Fact]
    public void Utilities_DoNotDependOnRuntimeContext()
    {
        string root = FindProjectRoot();
        string directory = Path.Combine(root, "src", "Utilities");
        foreach (string file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.TopDirectoryOnly))
        {
            Assert.DoesNotContain("RuntimeContext.Instance", File.ReadAllText(file));
        }
    }

    [Fact]
    public void WebAdapters_DoNotReachIntoRuntimeEntityState()
    {
        string root = FindProjectRoot();
        string directory = Path.Combine(root, "src", "Web");
        string[] forbidden =
        {
            "DataLock",
            ".Scripts",
            ".Queues",
            ".Users",
            "SnapshotScripts",
            "SnapshotQueues",
            "SnapshotUsers",
            "FindScript",
            "FindQueue",
            "FindUser",
        };

        foreach (string file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.TopDirectoryOnly))
        {
            string contents = File.ReadAllText(file);
            foreach (string marker in forbidden)
            {
                Assert.DoesNotContain(marker, contents);
            }
        }
    }

    [Fact]
    public void WebHandlers_DoNotCallOtherHandlers()
    {
        string root = FindProjectRoot();
        string directory = Path.Combine(root, "src", "Web");
        foreach (string file in Directory.EnumerateFiles(directory, "*Handler.cs", SearchOption.TopDirectoryOnly))
        {
            string contents = File.ReadAllText(file);
            Assert.DoesNotMatch(@"Api[A-Za-z]+Handler\.[A-Za-z]+", contents);
        }
    }

    [Fact]
    public void CommonInitializer_IsReadOnlyAndHostedInitializerOwnsRepair()
    {
        string root = FindProjectRoot();
        string common = File.ReadAllText(Path.Combine(root, "src", "Application", "RuntimeInitializer.cs"));
        string hosted = File.ReadAllText(Path.Combine(root, "src", "Application", "HostedRuntimeInitializer.cs"));

        Assert.Contains("ConfigLoadMode.ReadOnly", common);
        foreach (string marker in new[]
        {
            "ReloadData(",
            "DataStore.Save",
            "NormalizeEntityNames",
            "RecoverInterrupted",
            "ConfigWorkDirMaintenance",
            "TaskRegistration.SyncWithSettings",
        })
        {
            Assert.DoesNotContain(marker, common);
        }

        Assert.Contains("ConfigLoadMode.Repair", hosted);
        Assert.Contains("ReloadData()", hosted);
        Assert.Contains("RuntimeDataReconciler.Reconcile", hosted);
        Assert.Contains("RecoverInterrupted", hosted);
    }

    [Fact]
    public void RuntimeContext_DoesNotExposeEntityListsOrLegacyDataLock()
    {
        string root = FindProjectRoot();
        string source = File.ReadAllText(Path.Combine(root, "src", "RuntimeContext.cs"));
        Assert.DoesNotContain("DataLock", source);
        Assert.DoesNotMatch(@"public\s+(?:readonly\s+)?List<[^>]+>\s+(?:Scripts|Queues|Users)", source);
        Assert.DoesNotContain("SnapshotEffectiveScripts", source);
    }

    [Fact]
    public void RuntimeContext_ResolvesStateBackedQueryServices()
    {
        RuntimeContext context = RuntimeContext.Instance;

        Assert.NotNull(context.Resolve<ScriptQueries>());
        Assert.NotNull(context.Resolve<QueueQueries>());
        Assert.NotNull(context.Resolve<UserQueries>());
    }

    private static string FindProjectRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "src", "NexusPipeline.csproj")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new InvalidOperationException("无法定位 NexusPipeline 项目根目录");
    }
}
