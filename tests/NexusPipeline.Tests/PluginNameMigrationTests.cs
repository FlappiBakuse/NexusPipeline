using System.Text.Json.Nodes;
using NexusPipeline.App.Abstractions;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Plugins;
using NexusPipeline.Services.Notification;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class PluginNameMigrationTests
{
    [Theory]
    [InlineData("maastellasora", "maas")]
    [InlineData("MAASTELLASORA", "maas")]
    [InlineData(" maastellasora ", "maas")]
    [InlineData("maas", "maas")]
    [InlineData("bettergi", "bettergi")]
    public void Canonicalize_UsesCurrentPluginMachineName(string input, string expected)
    {
        Assert.Equal(expected, PluginNameMigration.Canonicalize(input));
    }

    [Fact]
    public async Task LegacyScopedStore_WritesIntoCanonicalNamespace()
    {
        string scope = $"migration-test-{Guid.NewGuid():N}";
        string canonicalPath = Path.Combine(AppPaths.ConfigDir, "plugins", "maas", "scopes", scope + ".json");
        string legacyPath = Path.Combine(AppPaths.ConfigDir, "plugins", "maastellasora", "scopes", scope + ".json");

        try
        {
            var store = new PluginScopedDataStore("maastellasora");
            await store.WriteJsonAsync(scope, new JsonObject { ["value"] = "preserved" });

            Assert.True(File.Exists(canonicalPath));
            Assert.False(File.Exists(legacyPath));
            JsonObject? loaded = await store.ReadJsonAsync(scope);
            Assert.Equal("preserved", loaded?["value"]?.GetValue<string>());
        }
        finally
        {
            if (File.Exists(canonicalPath))
            {
                File.Delete(canonicalPath);
            }
            if (File.Exists(legacyPath))
            {
                File.Delete(legacyPath);
            }
        }
    }

    [Fact]
    public void Apply_MigratesPersistedScriptBindingAcrossReload()
    {
        using var sandbox = new MigrationSandbox();
        RuntimeContext context = RuntimeContext.Instance;
        var script = new ScriptInstance
        {
            Id = "migration-script",
            Name = "旧插件脚本",
            PluginType = PluginNameMigration.LegacyMaaStellaSora,
        };
        DataStore.SaveScripts(new List<ScriptInstance> { script });
        context.EntityState.ReplaceLoadedState(new[] { script }, Array.Empty<DispatchQueue>(), Array.Empty<NexusUser>(), true);

        PluginNameMigration.Apply(context);

        Assert.Equal(PluginNameMigration.MaaStellaSora, Assert.Single(context.EntityState.SnapshotScripts()).PluginType);
        Assert.Equal(PluginNameMigration.MaaStellaSora, Assert.Single(DataStore.LoadScripts()).PluginType);
        context.ReloadData();
        Assert.Equal(PluginNameMigration.MaaStellaSora, Assert.Single(context.EntityState.SnapshotScripts()).PluginType);
    }

    [Fact]
    public void Apply_MigratesPluginPreferenceAndPreservesValueAcrossReload()
    {
        using var sandbox = new MigrationSandbox();
        AppSettings settings = new()
        {
            PluginPreferences = new Dictionary<string, PluginPreference>(StringComparer.OrdinalIgnoreCase)
            {
                [PluginNameMigration.LegacyMaaStellaSora] = new PluginPreference { Enabled = false },
            },
        };
        sandbox.SaveSettings(settings);

        PluginNameMigration.Apply(RuntimeContext.Instance);

        Assert.False(RuntimeContext.Instance.Settings.PluginPreferences[PluginNameMigration.MaaStellaSora].Enabled);
        Assert.DoesNotContain(
            RuntimeContext.Instance.Settings.PluginPreferences.Keys,
            key => key.Equals(PluginNameMigration.LegacyMaaStellaSora, StringComparison.OrdinalIgnoreCase));
        AppSettings reloaded = ConfigStore.Load(ConfigLoadMode.ReadOnly);
        Assert.False(reloaded.PluginPreferences[PluginNameMigration.MaaStellaSora].Enabled);
        Assert.DoesNotContain(
            reloaded.PluginPreferences.Keys,
            key => key.Equals(PluginNameMigration.LegacyMaaStellaSora, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Apply_CanonicalPreferenceWinsAndLegacyPreferenceIsRemovedOnConflict()
    {
        using var sandbox = new MigrationSandbox();
        AppSettings settings = new()
        {
            PluginPreferences = new Dictionary<string, PluginPreference>(StringComparer.OrdinalIgnoreCase)
            {
                [PluginNameMigration.MaaStellaSora] = new PluginPreference { Enabled = true },
                [PluginNameMigration.LegacyMaaStellaSora] = new PluginPreference { Enabled = false },
            },
        };
        sandbox.SaveSettings(settings);

        PluginNameMigration.Apply(RuntimeContext.Instance);

        Assert.True(RuntimeContext.Instance.Settings.PluginPreferences[PluginNameMigration.MaaStellaSora].Enabled);
        Assert.DoesNotContain(
            RuntimeContext.Instance.Settings.PluginPreferences.Keys,
            key => key.Equals(PluginNameMigration.LegacyMaaStellaSora, StringComparison.OrdinalIgnoreCase));
        AppSettings reloaded = ConfigStore.Load(ConfigLoadMode.ReadOnly);
        Assert.True(reloaded.PluginPreferences[PluginNameMigration.MaaStellaSora].Enabled);
        Assert.DoesNotContain(
            reloaded.PluginPreferences.Keys,
            key => key.Equals(PluginNameMigration.LegacyMaaStellaSora, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Apply_MovesScopedDirectoryAndManagedConfigFilesToCanonicalNames()
    {
        using var sandbox = new MigrationSandbox();
        string pluginsRoot = Path.Combine(AppPaths.ConfigDir, "plugins");
        string legacyDirectory = Path.Combine(pluginsRoot, PluginNameMigration.LegacyMaaStellaSora);
        string canonicalDirectory = Path.Combine(pluginsRoot, PluginNameMigration.MaaStellaSora);
        DeleteExact(legacyDirectory);
        DeleteExact(canonicalDirectory);
        DeleteExact(Path.Combine(pluginsRoot, "maas.json"));
        DeleteExact(Path.Combine(pluginsRoot, "maas.secrets.json"));
        DeleteExact(Path.Combine(pluginsRoot, "maastellasora.json"));
        DeleteExact(Path.Combine(pluginsRoot, "maastellasora.secrets.json"));
        Directory.CreateDirectory(legacyDirectory);
        File.WriteAllText(Path.Combine(legacyDirectory, "scope.json"), "{\"value\":\"legacy\"}");
        File.WriteAllText(Path.Combine(pluginsRoot, "maastellasora.json"), "{\"config\":\"legacy\"}");
        File.WriteAllText(Path.Combine(pluginsRoot, "maastellasora.secrets.json"), "{\"secret\":\"legacy\"}");

        PluginNameMigration.Apply(RuntimeContext.Instance);

        Assert.False(Directory.Exists(legacyDirectory));
        Assert.Equal("{\"value\":\"legacy\"}", File.ReadAllText(Path.Combine(canonicalDirectory, "scope.json")));
        Assert.Equal("{\"config\":\"legacy\"}", File.ReadAllText(Path.Combine(pluginsRoot, "maas.json")));
        Assert.Equal("{\"secret\":\"legacy\"}", File.ReadAllText(Path.Combine(pluginsRoot, "maas.secrets.json")));
        Assert.False(File.Exists(Path.Combine(pluginsRoot, "maastellasora.json")));
        Assert.False(File.Exists(Path.Combine(pluginsRoot, "maastellasora.secrets.json")));
    }

    [Fact]
    public void Apply_PreservesBothNamesWhenCanonicalStorageAlreadyExists()
    {
        using var sandbox = new MigrationSandbox();
        string pluginsRoot = Path.Combine(AppPaths.ConfigDir, "plugins");
        string legacyDirectory = Path.Combine(pluginsRoot, PluginNameMigration.LegacyMaaStellaSora);
        string canonicalDirectory = Path.Combine(pluginsRoot, PluginNameMigration.MaaStellaSora);
        DeleteExact(legacyDirectory);
        DeleteExact(canonicalDirectory);
        DeleteExact(Path.Combine(pluginsRoot, "maas.json"));
        DeleteExact(Path.Combine(pluginsRoot, "maastellasora.json"));
        Directory.CreateDirectory(legacyDirectory);
        Directory.CreateDirectory(canonicalDirectory);
        File.WriteAllText(Path.Combine(legacyDirectory, "scope.json"), "legacy");
        File.WriteAllText(Path.Combine(canonicalDirectory, "scope.json"), "canonical");
        File.WriteAllText(Path.Combine(pluginsRoot, "maastellasora.json"), "legacy-config");
        File.WriteAllText(Path.Combine(pluginsRoot, "maas.json"), "canonical-config");

        PluginNameMigration.Apply(RuntimeContext.Instance);

        Assert.True(Directory.Exists(legacyDirectory));
        Assert.Equal("legacy", File.ReadAllText(Path.Combine(legacyDirectory, "scope.json")));
        Assert.Equal("canonical", File.ReadAllText(Path.Combine(canonicalDirectory, "scope.json")));
        Assert.Equal("legacy-config", File.ReadAllText(Path.Combine(pluginsRoot, "maastellasora.json")));
        Assert.Equal("canonical-config", File.ReadAllText(Path.Combine(pluginsRoot, "maas.json")));
    }

    [Fact]
    public void PluginManager_ResolvesCanonicalNameToLegacyPackageDuringTransition()
    {
        string root = Path.Combine(Path.GetTempPath(), "np-legacy-plugin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "data"));
        File.WriteAllText(Path.Combine(root, "plugin.json"), new JsonObject
        {
            ["schemaVersion"] = 2,
            ["name"] = PluginNameMigration.LegacyMaaStellaSora,
            ["artifactName"] = "LegacyMaaStellaSora",
            ["displayName"] = "Legacy MaaStellaSora",
            ["version"] = "1.0.0",
            ["kind"] = "data-specialized",
            ["resolve"] = "data/resolve.json",
            ["judgeScript"] = "data/judge.js",
        }.ToJsonString());
        File.WriteAllText(Path.Combine(root, "data", "resolve.json"), "{}");
        File.WriteAllText(Path.Combine(root, "data", "judge.js"), "");
        DataSpecializedPlugin plugin = Assert.IsType<DataSpecializedPlugin>(DataSpecializedPlugin.Load(root));
        AppSettings settings = new();
        var manager = new PluginManager(
            () => settings,
            () => new NotificationDispatcher(new LocalSettingsProvider()),
            discoverData: () => new List<DataSpecializedPlugin> { plugin });

        try
        {
            manager.LoadAll();
            Assert.True(manager.IsKnownPlugin(PluginNameMigration.MaaStellaSora));
            Assert.True(manager.IsDataSpecializedPlugin(PluginNameMigration.MaaStellaSora));
            Assert.Equal("Active", manager.GetRuntimeState(PluginNameMigration.MaaStellaSora));
        }
        finally
        {
            manager.ShutdownAll();
            DeleteExact(root);
        }
    }

    private sealed class MigrationSandbox : IDisposable
    {
        private readonly RuntimeContext _context = RuntimeContext.Instance;
        private readonly AppSettings _settings;
        private readonly List<ScriptInstance> _scripts;
        private readonly List<DispatchQueue> _queues;
        private readonly List<NexusUser> _users;
        private readonly bool _scriptsAuthoritative;
        private readonly string _backupRoot = Path.Combine(Path.GetTempPath(), "np-migration-backup-" + Guid.NewGuid().ToString("N"));
        private readonly Dictionary<string, byte[]?> _fileBackups = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string?> _directoryBackups = new(StringComparer.OrdinalIgnoreCase);

        public MigrationSandbox()
        {
            _settings = _context.Settings.Clone();
            _scripts = _context.EntityState.SnapshotScripts();
            _queues = _context.EntityState.SnapshotQueues();
            _users = _context.EntityState.SnapshotUsers();
            _scriptsAuthoritative = _context.EntityState.LastScriptsLoadWasAuthoritative;
            Directory.CreateDirectory(_backupRoot);

            foreach (string file in FilesToSnapshot())
            {
                _fileBackups[file] = File.Exists(file) ? File.ReadAllBytes(file) : null;
            }
            int index = 0;
            foreach (string directory in DirectoriesToSnapshot())
            {
                if (Directory.Exists(directory))
                {
                    string backup = Path.Combine(_backupRoot, $"directory-{index++}");
                    CopyDirectory(directory, backup);
                    _directoryBackups[directory] = backup;
                }
                else
                {
                    _directoryBackups[directory] = null;
                }
            }
        }

        public void SaveSettings(AppSettings settings)
        {
            _context.ReplaceSettings(settings);
            ConfigStore.Save(settings);
        }

        public void Dispose()
        {
            foreach (string file in _fileBackups.Keys)
            {
                DeleteExact(file);
                byte[]? content = _fileBackups[file];
                if (content is not null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    File.WriteAllBytes(file, content);
                }
            }
            foreach ((string directory, string? backup) in _directoryBackups)
            {
                DeleteExact(directory);
                if (backup is not null)
                {
                    CopyDirectory(backup, directory);
                }
            }
            _context.ReplaceSettings(_settings);
            _context.EntityState.ReplaceLoadedState(_scripts, _queues, _users, _scriptsAuthoritative);
            DeleteExact(_backupRoot);
        }

        private static IEnumerable<string> FilesToSnapshot()
        {
            yield return AppPaths.ConfigPath;
            yield return AppPaths.ScriptsPath;
            string pluginsRoot = Path.Combine(AppPaths.ConfigDir, "plugins");
            yield return Path.Combine(pluginsRoot, "maas.json");
            yield return Path.Combine(pluginsRoot, "maas.secrets.json");
            yield return Path.Combine(pluginsRoot, "maastellasora.json");
            yield return Path.Combine(pluginsRoot, "maastellasora.secrets.json");
        }

        private static IEnumerable<string> DirectoriesToSnapshot()
        {
            string pluginsRoot = Path.Combine(AppPaths.ConfigDir, "plugins");
            yield return Path.Combine(pluginsRoot, PluginNameMigration.MaaStellaSora);
            yield return Path.Combine(pluginsRoot, PluginNameMigration.LegacyMaaStellaSora);
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.GetFiles(source))
            {
                string target = Path.Combine(destination, Path.GetFileName(file));
                File.Copy(file, target, overwrite: true);
            }
            foreach (string directory in Directory.GetDirectories(source))
            {
                CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
            }
        }
    }

    private sealed class LocalSettingsProvider : ISettingsProvider
    {
        public AppSettings Current { get; } = new();
    }

    private static void DeleteExact(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        else if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
