using System.Text.Json;
using NexusPipeline.Models;
using NexusPipeline.Utilities;

namespace NexusPipeline.Persistence;

/// <summary>
/// → 用户领域迁移器。
///
/// 迁移同时涉及 scripts.json、users.json 和多个用户数据目录，因此用 journal 记录计划与每个目录
/// 的完成状态。任意阶段重启都从 journal 继续，遇到 source/target 同时存在时直接拒绝启动，保留现场。
/// </summary>
internal static class UserModelMigration
{
    private const string Completed = "Completed";

    public static void EnsureMigrated()
    {
        if (File.Exists(AppPaths.UserModelMigrationPath))
        {
            MigrationJournal journal = LoadJournal(AppPaths.UserModelMigrationPath);
            if (!string.Equals(journal.Phase, Completed, StringComparison.Ordinal))
            {
                Resume(
                    AppPaths.ScriptsPath,
                    AppPaths.UsersPath,
                    AppPaths.DataDir,
                    AppPaths.UserModelMigrationPath,
                    journal);
            }
            return;
        }

        // users.json 是新模型的完成标记。没有 journal 时只可能是已完成迁移或新安装。
        if (File.Exists(AppPaths.UsersPath))
        {
            return;
        }

        Migrate(
            AppPaths.ScriptsPath,
            AppPaths.UsersPath,
            AppPaths.DataDir,
            AppPaths.UserModelMigrationPath,
            DateTime.Now);
    }

    /// <summary>供单元测试和恢复流程使用的路径隔离入口。</summary>
    internal static void Migrate(
        string scriptsPath,
        string usersPath,
        string dataDir,
        string journalPath,
        DateTime now)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(usersPath) ?? AppPaths.ConfigDir);
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath) ?? AppPaths.MigrationsDir);

        if (File.Exists(journalPath))
        {
            MigrationJournal journal = LoadJournal(journalPath);
            if (!string.Equals(journal.Phase, Completed, StringComparison.Ordinal))
            {
                Resume(scriptsPath, usersPath, dataDir, journalPath, journal);
            }
            return;
        }

        if (File.Exists(usersPath))
        {
            return;
        }

        MigrationJournal planned = BuildPlan(scriptsPath, dataDir, now);
        SaveJournal(journalPath, planned);
        Resume(scriptsPath, usersPath, dataDir, journalPath, planned);
    }

    private static MigrationJournal BuildPlan(string scriptsPath, string dataDir, DateTime now)
    {
        var journal = new MigrationJournal
        {
            Version = 1,
            Phase = "Planned",
            StartedAt = now,
        };

        if (!File.Exists(scriptsPath))
        {
            return journal;
        }

        string backup = scriptsPath + $".pre-v096-{now:yyyyMMddHHmmssfff}";
        if (!File.Exists(backup))
        {
            File.Copy(scriptsPath, backup, overwrite: false);
        }
        journal.ScriptsBackup = backup;

        List<ScriptInstance> scripts = LoadLegacyScripts(scriptsPath);
        Dictionary<string, NexusUser> byName = new(StringComparer.OrdinalIgnoreCase);
        foreach (ScriptInstance script in scripts.OrderBy(item => item.Index))
        {
            foreach (ScriptUser oldUser in script.Users)
            {
                if (!ScriptUserRule.IsValidName(oldUser.Name))
                {
                    throw new InvalidOperationException($"旧用户名称无法安全迁移：脚本 {script.Id} /「{oldUser.Name}」");
                }

                if (!byName.TryGetValue(oldUser.Name, out NexusUser? user))
                {
                    user = new NexusUser
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Index = byName.Count,
                        Name = oldUser.Name,
                        AutoCheckInEnabled = false,
                    };
                    byName.Add(user.Name, user);
                    journal.Users.Add(user);
                }

                if (user.Bindings.Any(binding => binding.ScriptInstanceId == script.Id))
                {
                    continue;
                }

                user.Bindings.Add(new UserScriptBinding
                {
                    ScriptInstanceId = script.Id,
                    Enabled = oldUser.Enabled,
                    PreRunScript = oldUser.PreRunScript,
                    PreRunOnceOnly = oldUser.PreRunOnceOnly,
                    PostRunScript = oldUser.PostRunScript,
                    PostRunOnFinalOnly = oldUser.PostRunOnFinalOnly,
                    // 旧版本没有二级开关，保持升级前的通知行为。
                    NotifyEnabled = true,
                    SmtpTo = "",
                });

                journal.Directories.Add(new MigrationDirectory
                {
                    ScriptId = script.Id,
                    OldName = oldUser.Name,
                    UserId = user.Id,
                    Source = Path.Combine(dataDir, script.Id, oldUser.Name),
                    Target = Path.Combine(dataDir, script.Id, user.Id),
                });
            }
        }
        return journal;
    }

    private static void Resume(
        string scriptsPath,
        string usersPath,
        string dataDir,
        string journalPath,
        MigrationJournal journal)
    {
        if (journal.Version != 1)
        {
            throw new InvalidOperationException($"无法识别 v0.9.6 用户迁移 journal 版本：{journal.Version}");
        }

        if (string.Equals(journal.Phase, "Planned", StringComparison.Ordinal))
        {
            foreach (MigrationDirectory item in journal.Directories)
            {
                bool sourceExists = Directory.Exists(item.Source);
                bool targetExists = Directory.Exists(item.Target);
                if (sourceExists && targetExists)
                {
                    throw new InvalidOperationException($"用户数据迁移发现 source/target 同时存在，已拒绝合并：{item.Source} ↔ {item.Target}");
                }
                if (sourceExists)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(item.Target) ?? dataDir);
                    Directory.Move(item.Source, item.Target);
                }
                // source 不存在且 target 存在表示上次移动成功但 journal 尚未来得及更新；两者都不存在表示该用户无数据。
                item.Moved = true;
                SaveJournal(journalPath, journal);
            }
            journal.Phase = "DataMigrated";
            SaveJournal(journalPath, journal);
        }

        if (string.Equals(journal.Phase, "DataMigrated", StringComparison.Ordinal))
        {
            JsonStore.SaveList(usersPath, journal.Users);
            journal.Phase = "UsersWritten";
            SaveJournal(journalPath, journal);
        }

        if (string.Equals(journal.Phase, "UsersWritten", StringComparison.Ordinal))
        {
            if (File.Exists(scriptsPath))
            {
                List<ScriptInstance> scripts = LoadLegacyScripts(scriptsPath);
                foreach (ScriptInstance script in scripts)
                {
                    // 新模型的权威用户数据位于 users.json；清空旧嵌套字段避免后续保存重复写入旧格式。
                    script.Users.Clear();
                }
                JsonStore.SaveList(scriptsPath, scripts);
            }
            journal.Phase = "ScriptsWritten";
            SaveJournal(journalPath, journal);
        }

        if (string.Equals(journal.Phase, "ScriptsWritten", StringComparison.Ordinal))
        {
            journal.Phase = Completed;
            journal.CompletedAt = DateTime.Now;
            SaveJournal(journalPath, journal);
            Logger.Info($"[迁移] v0.9.5 用户数据已迁移为全局用户模型（用户 {journal.Users.Count} 个，绑定 {journal.Directories.Count} 个）");
        }
    }

    private static List<ScriptInstance> LoadLegacyScripts(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<List<ScriptInstance>>(File.ReadAllText(path), JsonOpts.Default)
                ?? new List<ScriptInstance>();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"读取 v0.9.5 scripts.json 失败，已停止用户迁移：{ex.Message}", ex);
        }
    }

    private static MigrationJournal LoadJournal(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<MigrationJournal>(File.ReadAllText(path), JsonOpts.Default)
                ?? throw new InvalidOperationException("迁移 journal 为空");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"读取 v0.9.6 用户迁移 journal 失败，已拒绝启动：{ex.Message}", ex);
        }
    }

    private static void SaveJournal(string path, MigrationJournal journal)
    {
        JsonUtil.WriteAtomic(path, JsonSerializer.Serialize(journal, JsonOpts.Indented));
    }

    private sealed class MigrationJournal
    {
        public int Version { get; set; }

        public string Phase { get; set; } = "Planned";

        public DateTime StartedAt { get; set; }

        public DateTime? CompletedAt { get; set; }

        public string ScriptsBackup { get; set; } = "";

        public List<NexusUser> Users { get; set; } = new();

        public List<MigrationDirectory> Directories { get; set; } = new();
    }

    private sealed class MigrationDirectory
    {
        public string ScriptId { get; set; } = "";

        public string OldName { get; set; } = "";

        public string UserId { get; set; } = "";

        public string Source { get; set; } = "";

        public string Target { get; set; } = "";

        public bool Moved { get; set; }
    }
}
