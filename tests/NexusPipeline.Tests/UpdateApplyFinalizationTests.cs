using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services.Update;
using NexusPipeline.Utilities;
using Xunit;

namespace NexusPipeline.Tests;

/// <summary>更新域 L1：受限版本解析比较、releases JSON 解析、渠道过滤、主机白名单与当前 zip 合约。</summary>
public sealed class UpdateApplyFinalizationTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "np-update-final-" + Guid.NewGuid().ToString("N"));

    public UpdateApplyFinalizationTests()
    {
        Directory.CreateDirectory(_root);
    }

    private void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
        }
    }

    private void WriteTask(string mode, string version, string stagedDir)
    {
        // 直接写 AppRoot 任务标记（UpdateApply 收尾固定读 AppPaths）。
        string phase = mode switch
        {
            "completed" => UpdatePhase.Committed,
            "defer" => UpdatePhase.Deferred,
            "apply" => UpdatePhase.ApplyRequested,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "测试只写入当前更新阶段"),
        };
        new UpdateTask(mode, version, stagedDir, phase).Write();
    }

    private void WriteVersion(string version)
    {
        Directory.CreateDirectory(AppPaths.AppRoot);
        File.WriteAllText(AppPaths.UpdateVersionFile, version);
    }

    [Fact]
    public void Finalization_CompletedCleansMarkers()
    {
        try
        {
            string staging = Path.Combine(AppPaths.UpdateDir, "staging", "0.10.1");
            Directory.CreateDirectory(staging);
            Directory.CreateDirectory(AppPaths.UpdateBackupDir);
            WriteTask("completed", "0.10.1", staging);
            WriteVersion("0.10.1");

            bool exit = UpdateApply.RunStartupFinalization();

            Assert.False(exit);
            Assert.False(File.Exists(AppPaths.UpdateVersionFile));
            Assert.False(File.Exists(AppPaths.UpdateTaskFile));
            Assert.False(Directory.Exists(Path.Combine(AppPaths.UpdateDir, "staging")));
            Assert.False(Directory.Exists(AppPaths.UpdateBackupDir));
        }
        finally
        {
            DeleteExact(AppPaths.UpdateVersionFile);
            UpdateTask.Clear();
            DeleteExact(AppPaths.UpdateDir);
            DeleteExact(AppPaths.UpdateBackupDir);
        }
    }

    [Fact]
    public void Finalization_DeferRewritesToApplyAndRequestsExit()
    {
        try
        {
            string stagedDir = Path.Combine(AppPaths.UpdateDir, "staging", "0.10.1");
            Directory.CreateDirectory(stagedDir);
            File.WriteAllText(Path.Combine(stagedDir, "nexus-pipeline.exe"), "fake");
            WriteTask("defer", "0.10.1", stagedDir);
            List<string> launched = new();
            UpdateApply.LaunchApplyOverride = dir =>
            {
                launched.Add(dir);
                return true;
            };
            try
            {
                bool exit = UpdateApply.RunStartupFinalization();

                Assert.True(exit);
                Assert.Equal(stagedDir, Assert.Single(launched));
                UpdateTask? task = UpdateTask.Read();
                Assert.Equal("apply", task!.Mode);
            }
            finally
            {
                UpdateApply.LaunchApplyOverride = null;
            }
        }
        finally
        {
            UpdateTask.Clear();
            DeleteExact(AppPaths.UpdateDir);
            DeleteExact(AppPaths.UpdateBackupDir);
        }
    }

    [Fact]
    public void Finalization_IncompleteApplyRollsBackFromBackup()
    {
        try
        {
            // 制造「切换未完成」现场：备份里是旧 wwwroot，安装目录里是半成品新 wwwroot。
            string backupWww = Path.Combine(AppPaths.UpdateBackupDir, "wwwroot");
            Directory.CreateDirectory(backupWww);
            File.WriteAllText(Path.Combine(backupWww, "marker.txt"), "old");
            string installWww = Path.Combine(AppPaths.AppRoot, "wwwroot");
            Directory.CreateDirectory(installWww);
            File.WriteAllText(Path.Combine(installWww, "marker.txt"), "new-partial");
            string staging = Path.Combine(AppPaths.UpdateDir, "staging", "0.10.1");
            Directory.CreateDirectory(staging);
            WriteTask("apply", "0.10.1", staging);

            bool exit = UpdateApply.RunStartupFinalization();

            Assert.False(exit);
            Assert.Equal("old", File.ReadAllText(Path.Combine(installWww, "marker.txt")));
            Assert.False(File.Exists(AppPaths.UpdateTaskFile));
            Assert.False(Directory.Exists(AppPaths.UpdateBackupDir));
        }
        finally
        {
            UpdateTask.Clear();
            DeleteExact(AppPaths.UpdateDir);
            DeleteExact(AppPaths.UpdateBackupDir);
            DeleteExact(Path.Combine(AppPaths.AppRoot, "wwwroot"));
        }
    }

    [Fact]
    public void Finalization_ExecutableBackupRequestsRecoveryWorker()
    {
        try
        {
            string backupWww = Path.Combine(AppPaths.UpdateBackupDir, "wwwroot");
            Directory.CreateDirectory(backupWww);
            File.WriteAllText(Path.Combine(backupWww, "marker.txt"), "old");
            File.WriteAllText(Path.Combine(AppPaths.UpdateBackupDir, "nexus-pipeline.exe"), "old-exe");
            string staging = Path.Combine(AppPaths.UpdateDir, "staging", "0.10.1");
            Directory.CreateDirectory(staging);
            WriteTask("apply", "0.10.1", staging);

            bool launched = false;
            UpdateApply.LaunchRecoveryOverride = () =>
            {
                launched = true;
                return true;
            };
            try
            {
                bool exit = UpdateApply.RunStartupFinalization();

                Assert.True(exit);
                Assert.True(launched);
                Assert.True(File.Exists(AppPaths.UpdateTaskFile));
            }
            finally
            {
                UpdateApply.LaunchRecoveryOverride = null;
            }
        }
        finally
        {
            UpdateTask.Clear();
            DeleteExact(AppPaths.UpdateDir);
            DeleteExact(AppPaths.UpdateBackupDir);
        }
    }

    [Fact]
    public void Finalization_RollbackLeavesPluginDirectoryUntouched()
    {
        try
        {
            // 更新事务只负责程序文件与 wwwroot；插件目录中的用户内容保持原样。
            string backupWww = Path.Combine(AppPaths.UpdateBackupDir, "wwwroot");
            string backupPlugins = Path.Combine(AppPaths.UpdateBackupDir, "plugins", "bettergi");
            Directory.CreateDirectory(backupWww);
            Directory.CreateDirectory(backupPlugins);
            File.WriteAllText(Path.Combine(backupWww, "marker.txt"), "old-www");
            File.WriteAllText(Path.Combine(backupPlugins, "marker.txt"), "old-plugin");

            string installWww = Path.Combine(AppPaths.AppRoot, "wwwroot");
            string installPlugins = Path.Combine(AppPaths.AppRoot, "plugins");
            Directory.CreateDirectory(installWww);
            string installedPluginMarker = Path.Combine(installPlugins, "bettergi", "marker.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(installedPluginMarker)!);
            File.WriteAllText(Path.Combine(installWww, "marker.txt"), "new-partial-www");
            File.WriteAllText(installedPluginMarker, "user-plugin");

            string staging = Path.Combine(AppPaths.UpdateDir, "staging", "0.10.1");
            Directory.CreateDirectory(staging);
            WriteTask("apply", "0.10.1", staging);

            bool exit = UpdateApply.RunStartupFinalization();

            Assert.False(exit);
            Assert.Equal("old-www", File.ReadAllText(Path.Combine(installWww, "marker.txt")));
            Assert.Equal("user-plugin", File.ReadAllText(installedPluginMarker));
            Assert.False(File.Exists(AppPaths.UpdateTaskFile));
            Assert.False(Directory.Exists(AppPaths.UpdateBackupDir));
        }
        finally
        {
            UpdateTask.Clear();
            DeleteExact(AppPaths.UpdateDir);
            DeleteExact(AppPaths.UpdateBackupDir);
            DeleteExact(Path.Combine(AppPaths.AppRoot, "wwwroot"));
            DeleteExact(Path.Combine(AppPaths.AppRoot, "plugins"));
        }
    }

    private static void DeleteExact(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
