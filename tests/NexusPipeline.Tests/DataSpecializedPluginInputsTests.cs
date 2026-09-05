using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Extensibility;
using NexusPipeline.Plugins;
using Xunit;

namespace NexusPipeline.Tests;

/// <summary>resolve.json 用户输入变量（inputs + {input:名称} 占位符）契约：替换、回退、校验与组合限制。</summary>
public class DataSpecializedPluginInputsTests
{
    private static string MakeTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "np-inputs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>创建一个 BAAH 形态的最小插件：require BAAH.exe + inputs 声明 + {input:} 模板。</summary>
    private static (string PluginDir, string ScriptRoot) MakeBaahLikePlugin(string resolveJson)
    {
        string root = MakeTempDir();
        string pluginDir = Path.Combine(root, "plugin");
        string scriptRoot = Path.Combine(root, "BAAH");
        Directory.CreateDirectory(Path.Combine(pluginDir, "data"));
        Directory.CreateDirectory(scriptRoot);
        File.WriteAllText(Path.Combine(scriptRoot, "BAAH.exe"), "placeholder");
        Directory.CreateDirectory(Path.Combine(scriptRoot, "BAAH_CONFIGS"));
        File.WriteAllText(Path.Combine(scriptRoot, "BAAH_CONFIGS", "task.json"), "{}");
        File.WriteAllText(Path.Combine(scriptRoot, "BAAH_CONFIGS", "config.json"), "{}");
        File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            name = "inputs-test",
            artifactName = "InputsTest",
            displayName = "InputsTest",
            version = "0.1.0",
            kind = "data-specialized",
            resolve = "data/resolve.json",
            judgeScript = "data/judge.js",
        }));
        File.WriteAllText(Path.Combine(pluginDir, "data", "resolve.json"), resolveJson);
        File.WriteAllText(Path.Combine(pluginDir, "data", "judge.js"), "// judge");
        return (pluginDir, scriptRoot);
    }

    private const string BaahResolveJson = """
        {
          "inputs": [
            { "name": "config", "label": "BAAH 配置文件名", "description": "BAAH_CONFIGS 下的配置文件名",
              "default": "config.json", "required": true, "pattern": "^[A-Za-z0-9_\\-]+\\.json$" }
          ],
          "require": [ { "var": "main", "file": "BAAH.exe" } ],
          "paths": {
            "mainExe": "{main}",
            "args": "{input:config}",
            "configPath": "BAAH_CONFIGS/{input:config}",
            "logPath": ""
          }
        }
        """;

    [Fact]
    public void Resolve_SubstitutesInputs_IntoArgsAndConfigPath()
    {
        (string pluginDir, string scriptRoot) = MakeBaahLikePlugin(BaahResolveJson);
        var plugin = Assert.IsType<DataSpecializedPlugin>(DataSpecializedPlugin.Load(pluginDir));

        ScriptProfile? profile = plugin.Resolve(scriptRoot, new Dictionary<string, string> { ["config"] = "task.json" });

        Assert.NotNull(profile);
        Assert.Equal("task.json", profile!.Args);
        Assert.Equal(Path.Combine(scriptRoot, "BAAH_CONFIGS", "task.json"), profile.ConfigPath);
        Assert.Equal("", profile.LogPath);
        Assert.Equal(Path.Combine(scriptRoot, "BAAH.exe"), profile.MainExe);
    }

    [Fact]
    public void Resolve_MissingInput_FallsBackToDefault()
    {
        (string pluginDir, string scriptRoot) = MakeBaahLikePlugin(BaahResolveJson);
        var plugin = Assert.IsType<DataSpecializedPlugin>(DataSpecializedPlugin.Load(pluginDir));

        ScriptProfile? profile = plugin.Resolve(scriptRoot, null);

        Assert.NotNull(profile);
        Assert.Equal("config.json", profile!.Args);
        Assert.Equal(Path.Combine(scriptRoot, "BAAH_CONFIGS", "config.json"), profile.ConfigPath);
    }

    [Fact]
    public void Resolve_RequiredInputMissingWithoutDefault_ReturnsNull()
    {
        string resolveJson = """
            {
              "inputs": [ { "name": "config", "required": true } ],
              "require": [ { "var": "main", "file": "BAAH.exe" } ],
              "paths": { "mainExe": "{main}", "args": "{input:config}", "configPath": "BAAH_CONFIGS/{input:config}", "logPath": "" }
            }
            """;
        (string pluginDir, string scriptRoot) = MakeBaahLikePlugin(resolveJson);
        var plugin = Assert.IsType<DataSpecializedPlugin>(DataSpecializedPlugin.Load(pluginDir));

        Assert.Null(plugin.Resolve(scriptRoot, null));
        Assert.Null(plugin.Resolve(scriptRoot, new Dictionary<string, string> { ["config"] = "  " }));
    }

    [Fact]
    public void Resolve_ProvidedEmptyValue_FallsBackToDefault()
    {
        (string pluginDir, string scriptRoot) = MakeBaahLikePlugin(BaahResolveJson);
        var plugin = Assert.IsType<DataSpecializedPlugin>(DataSpecializedPlugin.Load(pluginDir));

        ScriptProfile? profile = plugin.Resolve(scriptRoot, new Dictionary<string, string> { ["config"] = " " });

        Assert.NotNull(profile);
        Assert.Equal("config.json", profile!.Args);
    }

    [Fact]
    public void Resolve_InputViolatingPattern_ReturnsNull()
    {
        (string pluginDir, string scriptRoot) = MakeBaahLikePlugin(BaahResolveJson);
        var plugin = Assert.IsType<DataSpecializedPlugin>(DataSpecializedPlugin.Load(pluginDir));

        Assert.Null(plugin.Resolve(scriptRoot, new Dictionary<string, string> { ["config"] = "带 空格.json" }));
    }

    [Fact]
    public void Resolve_InputWithPathSeparator_RejectedByBaseline()
    {
        string resolveJson = """
            {
              "inputs": [ { "name": "config", "required": true } ],
              "require": [ { "var": "main", "file": "BAAH.exe" } ],
              "paths": { "mainExe": "{main}", "args": "{input:config}", "configPath": "BAAH_CONFIGS/{input:config}", "logPath": "" }
            }
            """;
        (string pluginDir, string scriptRoot) = MakeBaahLikePlugin(resolveJson);
        var plugin = Assert.IsType<DataSpecializedPlugin>(DataSpecializedPlugin.Load(pluginDir));

        Assert.Null(plugin.Resolve(scriptRoot, new Dictionary<string, string> { ["config"] = "..\\evil.json" }));
        Assert.Null(plugin.Resolve(scriptRoot, new Dictionary<string, string> { ["config"] = "sub/dir.json" }));
    }

    [Fact]
    public void Resolve_UndeclaredInputReference_ReturnsNull()
    {
        string resolveJson = """
            {
              "require": [ { "var": "main", "file": "BAAH.exe" } ],
              "paths": { "mainExe": "{main}", "args": "{input:missing}", "configPath": "config", "logPath": "" }
            }
            """;
        (string pluginDir, string scriptRoot) = MakeBaahLikePlugin(resolveJson);
        var plugin = Assert.IsType<DataSpecializedPlugin>(DataSpecializedPlugin.Load(pluginDir));

        Assert.Null(plugin.Resolve(scriptRoot, new Dictionary<string, string> { ["missing"] = "x.json" }));
    }

    [Fact]
    public void Resolve_BindingPlaceholderMixedWithInput_ReturnsNull()
    {
        string resolveJson = """
            {
              "inputs": [ { "name": "config", "required": true } ],
              "require": [ { "var": "main", "file": "BAAH.exe" } ],
              "paths": { "mainExe": "{main}", "args": "{main} {input:config}", "configPath": "config", "logPath": "" }
            }
            """;
        (string pluginDir, string scriptRoot) = MakeBaahLikePlugin(resolveJson);
        var plugin = Assert.IsType<DataSpecializedPlugin>(DataSpecializedPlugin.Load(pluginDir));

        Assert.Null(plugin.Resolve(scriptRoot, new Dictionary<string, string> { ["config"] = "task.json" }));
    }

    [Fact]
    public void Resolve_MultipleBindingPlaceholders_StillRejected()
    {
        string resolveJson = """
            {
              "require": [
                { "var": "main", "file": "BAAH.exe" },
                { "var": "second", "file": "BAAH.exe" }
              ],
              "paths": { "mainExe": "{main}", "args": "{main} {second}", "configPath": "config", "logPath": "" }
            }
            """;
        (string pluginDir, string scriptRoot) = MakeBaahLikePlugin(resolveJson);
        var plugin = Assert.IsType<DataSpecializedPlugin>(DataSpecializedPlugin.Load(pluginDir));

        Assert.Null(plugin.Resolve(scriptRoot, null));
    }

    [Fact]
    public void Resolve_InvalidDeclarationName_ReturnsNull()
    {
        string resolveJson = """
            {
              "inputs": [ { "name": "1bad-name" } ],
              "require": [ { "var": "main", "file": "BAAH.exe" } ],
              "paths": { "mainExe": "{main}", "args": "{input:1bad-name}", "configPath": "config", "logPath": "" }
            }
            """;
        (string pluginDir, string scriptRoot) = MakeBaahLikePlugin(resolveJson);
        var plugin = Assert.IsType<DataSpecializedPlugin>(DataSpecializedPlugin.Load(pluginDir));

        Assert.Null(plugin.Resolve(scriptRoot, null));
    }

    [Fact]
    public void TryReadInputDeclarations_ReturnsParsedDeclarations()
    {
        (string pluginDir, _) = MakeBaahLikePlugin(BaahResolveJson);
        var plugin = Assert.IsType<DataSpecializedPlugin>(DataSpecializedPlugin.Load(pluginDir));

        Assert.True(plugin.TryReadInputDeclarations(out IReadOnlyList<PluginInputDeclaration>? declarations, out string? error));
        Assert.Null(error);
        PluginInputDeclaration declaration = Assert.Single(declarations!);
        Assert.Equal("config", declaration.Name);
        Assert.Equal("BAAH 配置文件名", declaration.Label);
        Assert.True(declaration.Required);
        Assert.Equal("config.json", declaration.Default);
        Assert.Contains("json", declaration.Pattern);
    }

    [Fact]
    public void ScriptInstance_PluginInputs_RoundTripsThroughClone()
    {
        var script = new NexusPipeline.Models.ScriptInstance
        {
            PluginType = "baah",
            PluginInputs = new Dictionary<string, string> { ["config"] = "task.json" },
        };

        NexusPipeline.Models.ScriptInstance clone = script.Clone();

        Assert.Equal("task.json", clone.PluginInputs["config"]);
    }

    /* ---------------- 复用配置候选推导（TryDiscoverConfigInputValues） ---------------- */

    /// <summary>创建 BetterGI 形态插件：User/OneDragon/{input:config}.json，目录预置两个配置。</summary>
    private static (string PluginDir, string ScriptRoot) MakeBetterGILikePlugin(string configRelativeDir, string suffix)
    {
        string root = MakeTempDir();
        string pluginDir = Path.Combine(root, "plugin");
        string scriptRoot = Path.Combine(root, "app");
        Directory.CreateDirectory(Path.Combine(pluginDir, "data"));
        Directory.CreateDirectory(Path.Combine(scriptRoot, configRelativeDir));
        File.WriteAllText(Path.Combine(scriptRoot, "main.exe"), "placeholder");
        File.WriteAllText(Path.Combine(scriptRoot, configRelativeDir, "默认配置" + suffix), "{}");
        File.WriteAllText(Path.Combine(scriptRoot, configRelativeDir, "大号日常" + suffix), "{}");
        File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            name = "discover-test",
            artifactName = "DiscoverTest",
            displayName = "DiscoverTest",
            version = "0.1.0",
            kind = "data-specialized",
            resolve = "data/resolve.json",
            judgeScript = "data/judge.js",
        }));
        File.WriteAllText(Path.Combine(pluginDir, "data", "resolve.json"), JsonSerializer.Serialize(new
        {
            inputs = new[] { new { name = "config", required = false } },
            require = new[] { new { var = "main", file = "main.exe" } },
            paths = new
            {
                mainExe = "{main}",
                args = "--start {input:config}",
                configPath = configRelativeDir.Replace('\\', '/') + "/{input:config}" + suffix,
                logPath = "",
            },
        }));
        File.WriteAllText(Path.Combine(pluginDir, "data", "judge.js"), "// judge");
        return (pluginDir, scriptRoot);
    }

    [Fact]
    public void DiscoverCandidates_StripsStaticSuffix_BetterGIShape()
    {
        // BetterGI：模板 User/OneDragon/{input:config}.json → 候选为不含 .json 后缀的配置名
        (string pluginDir, string scriptRoot) = MakeBetterGILikePlugin(Path.Combine("User", "OneDragon"), ".json");
        var plugin = Assert.IsType<DataSpecializedPlugin>(DataSpecializedPlugin.Load(pluginDir));

        Assert.True(plugin.TryDiscoverConfigInputValues(scriptRoot, out IReadOnlyList<string>? values));
        Assert.Equal(new[] { "大号日常", "默认配置" }, values!.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    [Fact]
    public void DiscoverCandidates_KeepsFullFileName_BAAHShape()
    {
        // BAAH：模板 BAAH_CONFIGS/{input:config}（后缀在输入值内）→ 候选为完整文件名
        (string pluginDir, string scriptRoot) = MakeBaahLikePlugin(BaahResolveJson);
        var plugin = Assert.IsType<DataSpecializedPlugin>(DataSpecializedPlugin.Load(pluginDir));

        Assert.True(plugin.TryDiscoverConfigInputValues(scriptRoot, out IReadOnlyList<string>? values));
        Assert.Contains("task.json", values!);
        Assert.Contains("config.json", values!);
    }

    [Fact]
    public void DiscoverCandidates_BindingPlaceholderTemplate_ReturnsEmpty()
    {
        string resolveJson = """
            {
              "require": [ { "var": "main", "file": "BAAH.exe" } ],
              "paths": { "mainExe": "{main}", "args": "", "configPath": "BAAH_CONFIGS", "logPath": "" }
            }
            """;
        (string pluginDir, string scriptRoot) = MakeBaahLikePlugin(resolveJson);
        var plugin = Assert.IsType<DataSpecializedPlugin>(DataSpecializedPlugin.Load(pluginDir));

        // 无输入引用的目录型 configPath：候选推导不适用
        Assert.False(plugin.TryDiscoverConfigInputValues(scriptRoot, out IReadOnlyList<string>? values));
        Assert.Empty(values!);
    }

    [Fact]
    public void DiscoverCandidates_UnsatisfiedRequire_ReturnsEmpty()
    {
        string resolveJson = """
            {
              "inputs": [ { "name": "config", "required": true } ],
              "require": [ { "var": "main", "file": "missing.exe" } ],
              "paths": { "mainExe": "{main}", "args": "{input:config}", "configPath": "BAAH_CONFIGS/{input:config}", "logPath": "" }
            }
            """;
        (string pluginDir, string scriptRoot) = MakeBaahLikePlugin(resolveJson);
        var plugin = Assert.IsType<DataSpecializedPlugin>(DataSpecializedPlugin.Load(pluginDir));

        Assert.False(plugin.TryDiscoverConfigInputValues(scriptRoot, out IReadOnlyList<string>? values));
        Assert.Empty(values!);
    }

    [Fact]
    public void DiscoverCandidates_MissingDirectory_ReturnsEmpty()
    {
        string resolveJson = """
            {
              "inputs": [ { "name": "config", "required": true } ],
              "require": [ { "var": "main", "file": "BAAH.exe" } ],
              "paths": { "mainExe": "{main}", "args": "{input:config}", "configPath": "no-such-dir/{input:config}", "logPath": "" }
            }
            """;
        (string pluginDir, string scriptRoot) = MakeBaahLikePlugin(resolveJson);
        var plugin = Assert.IsType<DataSpecializedPlugin>(DataSpecializedPlugin.Load(pluginDir));

        Assert.False(plugin.TryDiscoverConfigInputValues(scriptRoot, out IReadOnlyList<string>? values));
        Assert.Empty(values!);
    }

    /* ---------------- 自动绑定唯一配置（Resolve 解析层） ---------------- */

    [Fact]
    public void Resolve_EmptyInput_SingleConfigFile_AutoBinds()
    {
        (string pluginDir, string scriptRoot) = MakeBetterGILikePlugin(Path.Combine("User", "OneDragon"), ".json");
        File.Delete(Path.Combine(scriptRoot, "User", "OneDragon", "默认配置.json"));
        var plugin = Assert.IsType<DataSpecializedPlugin>(DataSpecializedPlugin.Load(pluginDir));

        ScriptProfile? profile = plugin.Resolve(scriptRoot, null);

        Assert.NotNull(profile);
        Assert.Equal("--start 大号日常", profile!.Args);
        Assert.Equal(Path.Combine(scriptRoot, "User", "OneDragon", "大号日常.json"), profile.ConfigPath);
    }

    [Fact]
    public void Resolve_StaleInput_SingleConfigFile_AdoptsAndSelfHeals()
    {
        // 输入指向的配置已被改名消失：目录内唯一配置自动绑定（自愈）
        (string pluginDir, string scriptRoot) = MakeBetterGILikePlugin(Path.Combine("User", "OneDragon"), ".json");
        File.Delete(Path.Combine(scriptRoot, "User", "OneDragon", "默认配置.json"));
        var plugin = Assert.IsType<DataSpecializedPlugin>(DataSpecializedPlugin.Load(pluginDir));

        ScriptProfile? profile = plugin.Resolve(scriptRoot, new Dictionary<string, string> { ["config"] = "已被改名的旧配置" });

        Assert.NotNull(profile);
        Assert.Equal("--start 大号日常", profile!.Args);
        Assert.Equal(Path.Combine(scriptRoot, "User", "OneDragon", "大号日常.json"), profile.ConfigPath);
    }

    [Fact]
    public void Resolve_InputTargetExists_KeepsDeclaredBinding()
    {
        // 声明目标存在时优先声明绑定（目录内还有其他配置也不影响）
        (string pluginDir, string scriptRoot) = MakeBetterGILikePlugin(Path.Combine("User", "OneDragon"), ".json");
        var plugin = Assert.IsType<DataSpecializedPlugin>(DataSpecializedPlugin.Load(pluginDir));

        ScriptProfile? profile = plugin.Resolve(scriptRoot, new Dictionary<string, string> { ["config"] = "大号日常" });

        Assert.NotNull(profile);
        Assert.Equal("--start 大号日常", profile!.Args);
        Assert.Equal(Path.Combine(scriptRoot, "User", "OneDragon", "大号日常.json"), profile.ConfigPath);
    }

    [Fact]
    public void Resolve_EmptyInput_MultipleConfigFiles_DoesNotGuess()
    {
        // 目录内有多个配置文件且输入为空：不猜测，configPath 保持未绑定（复用编辑启动时再询问）
        (string pluginDir, string scriptRoot) = MakeBetterGILikePlugin(Path.Combine("User", "OneDragon"), ".json");
        var plugin = Assert.IsType<DataSpecializedPlugin>(DataSpecializedPlugin.Load(pluginDir));

        ScriptProfile? profile = plugin.Resolve(scriptRoot, null);

        Assert.NotNull(profile);
        Assert.Equal("--start", profile!.Args.Trim());
        Assert.Equal(Path.Combine(scriptRoot, "User", "OneDragon", ".json"), profile.ConfigPath);
    }

    /// <summary>创建 OneDragon 形态的最小插件：configPath 绑定实例子目录（目录候选），pattern 过滤共享目录。</summary>
    private static (string PluginDir, string ScriptRoot) MakeOneDragonLikePlugin()
    {
        string root = MakeTempDir();
        string pluginDir = Path.Combine(root, "plugin");
        string scriptRoot = Path.Combine(root, "OneDragon");
        Directory.CreateDirectory(Path.Combine(pluginDir, "data"));
        Directory.CreateDirectory(scriptRoot);
        File.WriteAllText(Path.Combine(scriptRoot, "OneDragon-Launcher.exe"), "placeholder");
        Directory.CreateDirectory(Path.Combine(scriptRoot, "config", "01"));
        File.WriteAllText(Path.Combine(scriptRoot, "config", "one_dragon.yml"), "instance_list: []");
        Directory.CreateDirectory(Path.Combine(scriptRoot, "config", "auto_battle"));
        File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            name = "inputs-test",
            artifactName = "InputsTest",
            displayName = "InputsTest",
            version = "0.1.0",
            kind = "data-specialized",
            resolve = "data/resolve.json",
            judgeScript = "data/judge.js",
        }));
        File.WriteAllText(Path.Combine(pluginDir, "data", "resolve.json"), """
            {
              "inputs": [
                { "name": "instance", "label": "实例序号", "description": "config 下的实例目录名",
                  "required": false, "pattern": "^\\d{2}$" }
              ],
              "require": [ { "var": "main", "file": "OneDragon-Launcher.exe" } ],
              "paths": {
                "mainExe": "{main}",
                "args": "-o -c -i {input:instance}",
                "configPath": "config/{input:instance}",
                "logPath": ".log/log.txt"
              }
            }
            """);
        File.WriteAllText(Path.Combine(pluginDir, "data", "judge.js"), "// judge");
        return (pluginDir, scriptRoot);
    }

    [Fact]
    public void Resolve_EmptyInput_SingleInstanceDirectory_AutoBindsDirectoryCandidate()
    {
        // 目录候选：config 下只有一个两位序号实例目录时自动绑定，全局文件与共享目录被 pattern 过滤
        (string pluginDir, string scriptRoot) = MakeOneDragonLikePlugin();
        var plugin = Assert.IsType<DataSpecializedPlugin>(DataSpecializedPlugin.Load(pluginDir));

        ScriptProfile? profile = plugin.Resolve(scriptRoot, null);

        Assert.NotNull(profile);
        Assert.Equal("-o -c -i 01", profile!.Args);
        Assert.Equal(Path.Combine(scriptRoot, "config", "01"), profile.ConfigPath);
    }

    [Fact]
    public void Resolve_MultipleInstanceDirectories_BindsProvidedInput()
    {
        (string pluginDir, string scriptRoot) = MakeOneDragonLikePlugin();
        Directory.CreateDirectory(Path.Combine(scriptRoot, "config", "02"));
        var plugin = Assert.IsType<DataSpecializedPlugin>(DataSpecializedPlugin.Load(pluginDir));

        ScriptProfile? profile = plugin.Resolve(scriptRoot, new Dictionary<string, string> { ["instance"] = "02" });

        Assert.NotNull(profile);
        Assert.Equal("-o -c -i 02", profile!.Args);
        Assert.Equal(Path.Combine(scriptRoot, "config", "02"), profile.ConfigPath);
    }

    [Fact]
    public void Resolve_DirectoryCandidatePattern_FiltersNonInstanceEntries()
    {
        // 多个目录但只有 01/02 匹配 pattern：auto_battle 等共享目录不进入候选，多实例不猜测
        (string pluginDir, string scriptRoot) = MakeOneDragonLikePlugin();
        Directory.CreateDirectory(Path.Combine(scriptRoot, "config", "02"));
        var plugin = Assert.IsType<DataSpecializedPlugin>(DataSpecializedPlugin.Load(pluginDir));

        ScriptProfile? unbound = plugin.Resolve(scriptRoot, null);
        Assert.NotNull(unbound);
        // 未绑定时输入值为空、占位符替换为空串（与文件型未绑定语义一致：静态目录 + 空输入 + 空 staticTail）
        Assert.Equal(Path.Combine(scriptRoot, "config", "").TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, unbound!.ConfigPath);

        // 候选发现（复用编辑启动的选择清单）：只包含两位序号目录
        Assert.True(plugin.TryDiscoverConfigInputValues(scriptRoot, out IReadOnlyList<string> values));
        Assert.Equal(new[] { "01", "02" }, values.OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }
}
