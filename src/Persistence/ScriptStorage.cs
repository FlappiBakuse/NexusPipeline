using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Models;
using NexusPipeline.Services;
using NexusPipeline.Utilities;

namespace NexusPipeline.Persistence;

/// <summary>
/// 脚本实例持久化边界。
/// scripts.json 只保存实例声明；通用判断脚本源码由 JudgeScriptStore 单独管理，专项 profile 在运行时解析。
/// </summary>
internal sealed class ScriptStorage
{
    private static readonly HashSet<string> SpecializedDerivedProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "MainExe",
        "Args",
        "ConfigPath",
        "LogPath",
        "SuccessKeywords",
        "FailureKeywords",
        "JudgeScriptEnabled",
        "JudgeScriptLanguage",
        "JudgeScript",
        "AutoUpdateConfig",
    };

    private readonly string _configDir;
    private readonly string _scriptsPath;
    private readonly string _dataDir;
    private readonly string _migrationDir;
    private readonly string _migrationMarker;

    public ScriptStorage(string appRoot)
    {
        string root = Path.GetFullPath(appRoot);
        _configDir = Path.Combine(root, "config");
        _scriptsPath = Path.Combine(_configDir, "scripts.json");
        _dataDir = Path.Combine(root, "data");
        _migrationDir = Path.Combine(_configDir, "migrations", "v0.13.0");
        _migrationMarker = Path.Combine(_migrationDir, "completed.json");
        JudgeScripts = new JudgeScriptStore(Path.Combine(_configDir, "judge-scripts"));
    }

    public string ScriptsPath => _scriptsPath;

    public JudgeScriptStore JudgeScripts { get; }

    /// <summary>最近一次清单读取是否足以安全执行引用清理。</summary>
    public bool LastLoadAuthoritative { get; private set; }

    public List<ScriptInstance> LoadScripts()
    {
        LastLoadAuthoritative = false;
        TryMigrateLegacyFile();
        if (!File.Exists(_scriptsPath))
        {
            LastLoadAuthoritative = !HasCorruptScriptsBackup();
            JudgeScripts.QuarantineUnreferenced(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            return new List<ScriptInstance>();
        }

        JsonArray? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(_scriptsPath)) as JsonArray
                ?? throw new InvalidDataException("scripts.json 根节点必须是数组");
        }
        catch (Exception ex)
        {
            string backup = JsonStore.PreserveCorruptFile(_scriptsPath);
            Logger.Warn($"[警告] 解析 scripts.json 失败：{ex.Message}，原文件已保留为 {Path.GetFileName(backup)}");
            return new List<ScriptInstance>();
        }

        var scripts = new List<ScriptInstance>();
        bool authoritative = true;
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonNode? item in root)
        {
            if (item is not JsonObject obj)
            {
                authoritative = false;
                Logger.Warn("[脚本] scripts.json 中发现非对象条目，已忽略。");
                continue;
            }
            try
            {
                ScriptInstance? script = obj.Deserialize<ScriptInstance>(JsonOpts.Default);
                if (script is null)
                {
                    authoritative = false;
                    continue;
                }
                if (string.IsNullOrWhiteSpace(script.Id))
                {
                    authoritative = false;
                    script.Id = Guid.NewGuid().ToString("N");
                }
                if (string.IsNullOrWhiteSpace(script.PluginType))
                {
                    LoadGenericJudgeScript(script);
                    ConfigStoreMetadata.SeedLegacyStoreMetadata(_dataDir, script.Id, script.ConfigPath, "");
                    if (!string.IsNullOrWhiteSpace(script.JudgeScript))
                    {
                        referenced.Add(Path.GetFullPath(JudgeScripts.GetPath(script.Id, script.JudgeScriptLanguage)));
                    }
                }
                else
                {
                    // 兼容迁移失败或外部手工编辑的旧文件；插件派生字段不能重新成为持久化来源。
                    ClearSpecializedDerivedFields(script);
                }
                scripts.Add(script);
            }
            catch (Exception ex)
            {
                authoritative = false;
                Logger.Warn($"[脚本] 读取 scripts.json 条目失败（已忽略）：{ex.Message}");
            }
        }
        LastLoadAuthoritative = authoritative;
        // 启动清理只把未引用源码移入隔离区，保留可人工恢复的副本。
        JudgeScripts.QuarantineUnreferenced(referenced);
        return scripts;
    }

    public void SaveScripts(List<ScriptInstance> scripts)
    {
        Directory.CreateDirectory(_configDir);
        var records = new JsonArray();
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var assetBackups = new List<JudgeAssetBackup>();
        var capturedAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (ScriptInstance script in scripts)
            {
                string id = string.IsNullOrWhiteSpace(script.Id)
                    ? (script.Id = Guid.NewGuid().ToString("N"))
                    : script.Id;
                if (string.IsNullOrWhiteSpace(script.PluginType)
                    && !string.IsNullOrWhiteSpace(script.JudgeScript))
                {
                    string language = JudgeScriptStore.NormalizeLanguage(script.JudgeScriptLanguage);
                    string assetPath = JudgeScripts.GetPath(id, language);
                    if (capturedAssets.Add(assetPath))
                    {
                        bool existed = File.Exists(assetPath);
                        assetBackups.Add(new JudgeAssetBackup(
                            id,
                            language,
                            assetPath,
                            existed,
                            existed ? File.ReadAllText(assetPath) : ""));
                    }
                    JudgeScripts.SaveAtomic(id, language, script.JudgeScript);
                    referenced.Add(Path.GetFullPath(assetPath));
                    script.JudgeScriptLanguage = language;
                }

                JsonObject record = ToPersistentRecord(script);
                records.Add(record);
            }

            // 源码先写入，清单最后原子替换；清单写入失败时回滚本次已替换的源码资产。
            JsonUtil.WriteAtomic(_scriptsPath, records.ToJsonString(JsonOpts.Indented));
        }
        catch
        {
            RestoreAssetBackups(assetBackups);
            throw;
        }

        JudgeScripts.QuarantineUnreferenced(referenced);
    }

    /// <summary>保存成功后把运行时临时 profile 从共享列表收敛回声明，避免内存状态重新成为持久化来源。</summary>
    internal static void NormalizeInMemoryDeclarations(IEnumerable<ScriptInstance> scripts)
    {
        foreach (ScriptInstance script in scripts)
        {
            if (!string.IsNullOrWhiteSpace(script.PluginType))
            {
                ClearSpecializedDerivedFields(script);
            }
        }
    }

    private void LoadGenericJudgeScript(ScriptInstance script)
    {
        string language = JudgeScriptStore.NormalizeLanguage(script.JudgeScriptLanguage);
        string? source = JudgeScripts.Load(script.Id, language);
        if (source is not null)
        {
            script.JudgeScript = source;
            script.JudgeScriptLanguage = language;
        }
        else if (!string.IsNullOrWhiteSpace(script.JudgeScriptLanguage))
        {
            script.JudgeScriptLanguage = language;
        }
    }

    private void TryMigrateLegacyFile()
    {
        if (!File.Exists(_scriptsPath))
        {
            return;
        }

        JsonArray? legacy;
        try
        {
            legacy = JsonNode.Parse(File.ReadAllText(_scriptsPath)) as JsonArray
                ?? throw new InvalidDataException("scripts.json 根节点必须是数组");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[脚本迁移] 无法读取 scripts.json，保留原文件等待下次启动重试：{ex.Message}");
            return;
        }

        if (!NeedsMigration(legacy))
        {
            return;
        }

        string backupDir = Path.Combine(_migrationDir, $"{DateTime.Now:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(backupDir);
            string backupPath = Path.Combine(backupDir, "scripts.json");
            File.Copy(_scriptsPath, backupPath, overwrite: false);

            var migrated = new JsonArray();
            foreach (JsonNode? item in legacy)
            {
                if (item is not JsonObject source)
                {
                    throw new InvalidDataException("scripts.json 中存在非对象脚本条目");
                }

                JsonObject record = CloneObject(source);
                ScriptInstance script = record.Deserialize<ScriptInstance>(JsonOpts.Default) ?? new ScriptInstance();
                if (string.IsNullOrWhiteSpace(script.Id))
                {
                    script.Id = Guid.NewGuid().ToString("N");
                    SetProperty(record, "Id", script.Id);
                }

                string? inlineJudge = GetString(record, "JudgeScript");
                if (string.IsNullOrWhiteSpace(script.PluginType))
                {
                    if (!string.IsNullOrWhiteSpace(inlineJudge))
                    {
                        string language = JudgeScriptStore.NormalizeLanguage(GetString(record, "JudgeScriptLanguage"));
                        SaveMigratedJudgeScript(script.Id, language, inlineJudge);
                        SetProperty(record, "JudgeScriptLanguage", language);
                    }
                    RemoveProperty(record, "JudgeScript");
                }
                else
                {
                    ConfigStoreMetadata.SeedLegacyStoreMetadata(
                        _dataDir,
                        script.Id,
                        script.ConfigPath,
                        script.PluginType);
                    foreach (string property in SpecializedDerivedProperties)
                    {
                        RemoveProperty(record, property);
                    }
                }
                migrated.Add(record);
            }

            JsonUtil.WriteAtomic(_scriptsPath, migrated.ToJsonString(JsonOpts.Indented));
            Directory.CreateDirectory(_migrationDir);
            JsonUtil.WriteAtomic(
                _migrationMarker,
                $"{{\"version\":\"0.13.0\",\"migratedAt\":\"{DateTimeOffset.UtcNow:O}\"}}");
            Logger.Info($"scripts.json 已完成 v0.12.9 → v0.13.0 持久化迁移，旧文件备份于：{backupPath}");
        }
        catch (Exception ex)
        {
            Logger.Error($"[脚本迁移] v0.13.0 迁移失败，原 scripts.json 保留未替换：{ex.Message}");
        }
    }

    private static bool NeedsMigration(JsonArray root)
    {
        foreach (JsonNode? item in root)
        {
            if (item is not JsonObject obj)
            {
                continue;
            }
            string pluginType = GetString(obj, "PluginType");
            if (FindPropertyName(obj, "JudgeScript") is not null)
            {
                return true;
            }
            if (!string.IsNullOrWhiteSpace(pluginType)
                && SpecializedDerivedProperties.Any(property => FindPropertyName(obj, property) is not null))
            {
                return true;
            }
        }
        return false;
    }

    private static JsonObject ToPersistentRecord(ScriptInstance script)
    {
        JsonObject record = JsonSerializer.SerializeToNode(script, JsonOpts.Indented)?.AsObject()
            ?? new JsonObject();
        RemoveProperty(record, "JudgeScript");
        if (!string.IsNullOrWhiteSpace(script.PluginType))
        {
            foreach (string property in SpecializedDerivedProperties)
            {
                RemoveProperty(record, property);
            }
        }
        return record;
    }

    private static void ClearSpecializedDerivedFields(ScriptInstance script)
    {
        script.MainExe = "";
        script.Args = "";
        script.ConfigPath = "";
        script.LogPath = "";
        script.SuccessKeywords = "";
        script.FailureKeywords = "";
        script.JudgeScriptEnabled = false;
        script.JudgeScriptLanguage = "";
        script.JudgeScript = "";
        script.AutoUpdateConfig = true;
    }

    private static JsonObject CloneObject(JsonObject source)
    {
        return JsonNode.Parse(source.ToJsonString())?.AsObject() ?? new JsonObject();
    }

    private void SaveMigratedJudgeScript(string scriptId, string language, string content)
    {
        if (JudgeScripts.Exists(scriptId, language))
        {
            string? existing = JudgeScripts.Load(scriptId, language);
            if (string.Equals(existing, content, StringComparison.Ordinal))
            {
                return;
            }

            // 迁移中的旧 scripts.json 是本次迁移权威源；已有但不同的资产先隔离，保留人工恢复机会。
            string? conflict = JudgeScripts.MoveToOrphaned(scriptId, language);
            if (conflict is null)
            {
                throw new IOException($"判断脚本迁移发生资产冲突且无法隔离：{scriptId}{JudgeScriptStore.Extension(language)}");
            }
            Logger.Warn($"判断脚本迁移发现内容冲突，旧资产已隔离：{conflict}");
        }
        JudgeScripts.SaveAtomic(scriptId, language, content);
    }

    private void RestoreAssetBackups(IEnumerable<JudgeAssetBackup> backups)
    {
        foreach (JudgeAssetBackup backup in backups.Reverse())
        {
            try
            {
                if (backup.Existed)
                {
                    JudgeScripts.SaveAtomic(backup.ScriptId, backup.Language, backup.Content);
                }
                else if (File.Exists(backup.Path))
                {
                    File.Delete(backup.Path);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[判断脚本] 保存失败后的资产回滚失败（{backup.Path}）：{ex.Message}");
            }
        }
    }

    private sealed record JudgeAssetBackup(
        string ScriptId,
        string Language,
        string Path,
        bool Existed,
        string Content);

    private static string GetString(JsonObject obj, string property)
    {
        return obj[FindPropertyName(obj, property) ?? property].Str();
    }

    private static void SetProperty(JsonObject obj, string property, string value)
    {
        string actual = FindPropertyName(obj, property) ?? property;
        obj[actual] = value;
    }

    private static void RemoveProperty(JsonObject obj, string property)
    {
        string? actual = FindPropertyName(obj, property);
        if (actual is not null)
        {
            obj.Remove(actual);
        }
    }

    private static string? FindPropertyName(JsonObject obj, string property)
    {
        return obj.Select(pair => pair.Key)
            .FirstOrDefault(key => string.Equals(key, property, StringComparison.OrdinalIgnoreCase));
    }

    private bool HasCorruptScriptsBackup()
    {
        return Directory.Exists(_configDir)
            && Directory.GetFiles(
                _configDir,
                Path.GetFileName(_scriptsPath) + ".corrupt-*",
                SearchOption.TopDirectoryOnly).Length > 0;
    }
}
