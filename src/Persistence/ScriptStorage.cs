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

    public ScriptStorage(string appRoot)
    {
        string root = Path.GetFullPath(appRoot);
        _configDir = Path.Combine(root, "config");
        _scriptsPath = Path.Combine(_configDir, "scripts.json");
        JudgeScripts = new JudgeScriptStore(Path.Combine(_configDir, "judge-scripts"));
    }

    public string ScriptsPath => _scriptsPath;

    public JudgeScriptStore JudgeScripts { get; }

    /// <summary>最近一次清单读取是否足以安全执行引用清理。</summary>
    public bool LastLoadAuthoritative { get; private set; }

    public List<ScriptInstance> LoadScripts()
    {
        LastLoadAuthoritative = false;
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
                    if (!string.IsNullOrWhiteSpace(script.JudgeScript))
                    {
                        referenced.Add(Path.GetFullPath(JudgeScripts.GetPath(script.Id, script.JudgeScriptLanguage)));
                    }
                }
                else
                {
                    // 专项 profile 的派生字段不能重新成为持久化来源。
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
        // JudgeScript 不再作为 scripts.json 的内嵌旧格式来源；仅加载当前独立资产。
        script.JudgeScript = "";
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

    private static JsonObject ToPersistentRecord(ScriptInstance script)
    {
        JsonObject record = JsonSerializer.SerializeToNode(script, JsonOpts.Indented)?.AsObject()
            ?? new JsonObject();
        record.Remove("JudgeScript");
        if (!string.IsNullOrWhiteSpace(script.PluginType))
        {
            foreach (string property in SpecializedDerivedProperties)
            {
                record.Remove(property);
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

    private bool HasCorruptScriptsBackup()
    {
        return Directory.Exists(_configDir)
            && Directory.GetFiles(
                _configDir,
                Path.GetFileName(_scriptsPath) + ".corrupt-*",
                SearchOption.TopDirectoryOnly).Length > 0;
    }
}
