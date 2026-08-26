using System.Text;
using NexusPipeline.Cli;
using NexusPipeline.Persistence;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class RuntimeStateLayoutTests
{
    [Fact]
    public void MigrateLegacySchedulerState_MovesFileWithoutChangingContent()
    {
        string root = NewTempDir();
        try
        {
            var layout = new RuntimeStateLayout(root);
            Directory.CreateDirectory(root);
            string content = "{\"LastSchedulerCheck\":\"2026-08-26T10:00:00Z\",\"Occurrences\":[]}";
            File.WriteAllText(layout.LegacySchedulerStatePath, content, new UTF8Encoding(false));

            layout.EnsureMigrated();

            Assert.False(File.Exists(layout.LegacySchedulerStatePath));
            Assert.Equal(content, File.ReadAllText(layout.SchedulerStatePath));
            Assert.True(Directory.Exists(layout.RuntimeDir));
            Assert.True(Directory.Exists(layout.StateDir));
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    [Fact]
    public void MigrateLegacySchedulerState_ConflictKeepsNewAndArchivesLegacy()
    {
        string root = NewTempDir();
        try
        {
            var layout = new RuntimeStateLayout(root);
            Directory.CreateDirectory(layout.StateDir);
            File.WriteAllText(layout.SchedulerStatePath, "new-state", Encoding.UTF8);
            File.WriteAllText(layout.LegacySchedulerStatePath, "legacy-state", Encoding.UTF8);

            layout.EnsureMigrated();

            Assert.Equal("new-state", File.ReadAllText(layout.SchedulerStatePath));
            Assert.False(File.Exists(layout.LegacySchedulerStatePath));
            string[] recovery = Directory.GetFiles(layout.RecoveryDir, "scheduler-state.legacy-conflict-*.json");
            string archived = Assert.Single(recovery);
            Assert.Equal("legacy-state", File.ReadAllText(archived));
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    [Fact]
    public void EnsureMigrated_IsIdempotentAndCleansEphemeralLegacyMarkers()
    {
        string root = NewTempDir();
        try
        {
            var layout = new RuntimeStateLayout(root);
            Directory.CreateDirectory(layout.StateDir);
            File.WriteAllText(layout.LegacySchedulerStatePath, "legacy-state", Encoding.UTF8);
            File.WriteAllText(layout.LegacyServicePidPath, "1234", Encoding.ASCII);
            File.WriteAllText(layout.LegacyWebPortPath, "58888", Encoding.ASCII);

            layout.EnsureMigrated();
            layout.EnsureMigrated();

            Assert.False(File.Exists(layout.LegacyServicePidPath));
            Assert.False(File.Exists(layout.LegacyWebPortPath));
            Assert.Equal("legacy-state", File.ReadAllText(layout.SchedulerStatePath));
            Assert.True(!Directory.Exists(layout.RecoveryDir)
                || Directory.GetFiles(layout.RecoveryDir, "scheduler-state.legacy-conflict-*.json").Length == 0);
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    [Theory]
    [InlineData("58731", 58731)]
    [InlineData("  60000\r\n", 60000)]
    [InlineData("80", null)]
    [InlineData("not-a-port", null)]
    public void ReadLegacyWebPort_ValidatesPortRange(string content, int? expected)
    {
        string root = NewTempDir();
        try
        {
            var layout = new RuntimeStateLayout(root);
            File.WriteAllText(layout.LegacyWebPortPath, content, Encoding.ASCII);

            Assert.Equal(expected, layout.ReadLegacyWebPort());
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    [Fact]
    public void CandidatePorts_PrefersCurrentMarkerThenLegacyMarkerThenConfiguredRange()
    {
        string root = NewTempDir();
        try
        {
            string current = Path.Combine(root, ".nxp", "runtime", "web.port");
            string legacy = Path.Combine(root, "web.port");
            Directory.CreateDirectory(Path.GetDirectoryName(current)!);
            File.WriteAllText(current, "58800", Encoding.ASCII);
            File.WriteAllText(legacy, "58801", Encoding.ASCII);

            int[] ports = CliTransport.CandidatePorts(58731, current, legacy).Take(4).ToArray();

            Assert.Equal(new[] { 58800, 58801, 58731, 58732 }, ports);
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
