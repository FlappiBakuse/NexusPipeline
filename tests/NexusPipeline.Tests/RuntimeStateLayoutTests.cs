using System.Text;
using NexusPipeline.Cli;
using NexusPipeline.Persistence;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class RuntimeStateLayoutTests
{
    [Fact]
    public void EnsureDirectories_CreatesCurrentRuntimeAndStateDirectories()
    {
        string root = NewTempDir();
        try
        {
            var layout = new RuntimeStateLayout(root);

            layout.EnsureDirectories();

            Assert.True(Directory.Exists(layout.InternalDir));
            Assert.True(Directory.Exists(layout.RuntimeDir));
            Assert.True(Directory.Exists(layout.StateDir));
            Assert.StartsWith(layout.InternalDir, layout.ServicePidPath, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(layout.InternalDir, layout.WebPortPath, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(layout.InternalDir, layout.SchedulerStatePath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    [Fact]
    public void CurrentRuntimePaths_AreStableAndDoNotUseInstallRootMarkers()
    {
        string root = NewTempDir();
        try
        {
            var layout = new RuntimeStateLayout(root);

            Assert.Equal(Path.Combine(root, ".nxp"), layout.InternalDir);
            Assert.Equal(Path.Combine(layout.RuntimeDir, "service.pid"), layout.ServicePidPath);
            Assert.Equal(Path.Combine(layout.RuntimeDir, "web.port"), layout.WebPortPath);
            Assert.Equal(Path.Combine(layout.StateDir, "scheduler-state.json"), layout.SchedulerStatePath);
            Assert.False(File.Exists(Path.Combine(root, "web.port")));
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    [Fact]
    public void CandidatePorts_PrefersCurrentMarkerThenConfiguredRange()
    {
        string root = NewTempDir();
        try
        {
            string current = Path.Combine(root, ".nxp", "runtime", "web.port");
            Directory.CreateDirectory(Path.GetDirectoryName(current)!);
            File.WriteAllText(current, "58800", new UTF8Encoding(false));

            int[] ports = CliTransport.CandidatePorts(58731, current).Take(4).ToArray();

            Assert.Equal(new[] { 58800, 58731, 58732, 58733 }, ports);
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    [Fact]
    public void CandidatePorts_IgnoresInstallRootPortMarker()
    {
        string root = NewTempDir();
        try
        {
            string current = Path.Combine(root, ".nxp", "runtime", "web.port");
            Directory.CreateDirectory(Path.GetDirectoryName(current)!);
            File.WriteAllText(Path.Combine(root, "web.port"), "58801", new UTF8Encoding(false));

            int[] ports = CliTransport.CandidatePorts(58731, current).Take(3).ToArray();

            Assert.Equal(new[] { 58731, 58732, 58733 }, ports);
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    private static string NewTempDir()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-runtime-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTempDir(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
