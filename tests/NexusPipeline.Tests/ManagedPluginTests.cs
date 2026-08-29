using System.Text.Json;
using NexusPipeline.App.Abstractions;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Plugins;
using NexusPipeline.Services;
using NexusPipeline.Services.Notification;
using NexusPipeline.TestPlugin;
using NexusPipeline.Utilities;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class ManagedPluginTests
{
    [Fact]
    public void ManagedPlugin_DefaultDisabled_EnableAfterReload_AndHostServicesWork()
    {
        string root = CreatePluginDirectory("fixture-managed", typeof(NexusPipeline.TestPlugin.TestPlugin));
        var settings = new AppSettings();
        var manager = new PluginManager(
            () => settings,
            () => new NotificationDispatcher(new FixtureSettingsProvider()));

        try
        {
            manager.LoadAll();
            Assert.Equal("Disabled", manager.GetRuntimeState("fixture-managed"));
            Assert.False(manager.IsEnabled("fixture-managed"));

            string statePath = PluginStatePath("fixture-managed");
            Assert.False(File.Exists(statePath));
            Assert.True(manager.SetEnabled("fixture-managed", true));

            manager.LoadAll();
            Assert.Equal("Active", manager.GetRuntimeState("fixture-managed"));
            Assert.True(manager.IsConfiguredEnabled("fixture-managed"));
            Assert.True(manager.IsEnabled("fixture-managed"));
            PluginUserListBadgeRegistration badge = Assert.Single(manager.UserListBadgeContributions);
            Assert.Equal("fixture-badge", badge.Contribution.Id);
            Assert.True(WaitUntil(() => ReadState(statePath)?.JobRan == true, TimeSpan.FromSeconds(3)));
            Assert.True(ReadState(statePath)?.Initialized);
            Assert.True(ReadState(statePath)?.Started);
            if (CanUseDpapi())
            {
                Assert.Equal("fixture-secret", ReadSecret("fixture-managed", "fixture-token"));
            }
        }
        finally
        {
            manager.ShutdownAll();
            Assert.Empty(manager.UserListBadgeContributions);
            ReleasePluginContexts();
            DeletePluginDirectory(root);
        }

        Assert.True(ReadState(PluginStatePath("fixture-managed"))?.Stopped);
    }

    [Fact]
    public void ManagedPlugin_IncompatibleApiAndInitializationFailure_AreReportedWithoutActivation()
    {
        string incompatibleRoot = CreatePluginDirectory("fixture-incompatible", typeof(NexusPipeline.TestPlugin.TestPlugin), apiVersion: "2.0");
        string failingRoot = CreatePluginDirectory("fixture-failing", typeof(FailingPlugin));
        var settings = new AppSettings
        {
            PluginPreferences = new Dictionary<string, PluginPreference>(StringComparer.OrdinalIgnoreCase)
            {
                ["fixture-incompatible"] = new PluginPreference { Enabled = true },
                ["fixture-failing"] = new PluginPreference { Enabled = true },
            },
        };
        var manager = new PluginManager(
            () => settings,
            () => new NotificationDispatcher(new FixtureSettingsProvider()));

        try
        {
            manager.LoadAll();
            Assert.Equal("Incompatible", manager.GetRuntimeState("fixture-incompatible"));
            Assert.Contains("API", manager.GetRuntimeError("fixture-incompatible"));
            Assert.False(File.Exists(PluginStatePath("fixture-incompatible")));

            Assert.Equal("InitFailed", manager.GetRuntimeState("fixture-failing"));
            Assert.Contains("fixture init failure", manager.GetRuntimeError("fixture-failing"));
            Assert.False(manager.IsEnabled("fixture-failing"));
        }
        finally
        {
            manager.ShutdownAll();
            ReleasePluginContexts();
            DeletePluginDirectory(incompatibleRoot);
            DeletePluginDirectory(failingRoot);
        }
    }

    [Fact]
    public void FrontendDescriptor_IsPublishedForActivePluginWithoutConfirmation()
    {
        const string name = "fixture-frontend";
        string root = CreatePluginDirectory(name, typeof(NexusPipeline.TestPlugin.TestPlugin), frontend: true);
        var settings = new AppSettings
        {
            PluginPreferences = new Dictionary<string, PluginPreference>(StringComparer.OrdinalIgnoreCase)
            {
                [name] = new PluginPreference { Enabled = true },
            },
        };
        var manager = new PluginManager(
            () => settings,
            () => new NotificationDispatcher(new FixtureSettingsProvider()));

        try
        {
            manager.LoadAll();
            Assert.True(manager.IsEnabled(name));
            Assert.True(manager.HasFrontend(name));
            Assert.Single(manager.FrontendDescriptors);
            Assert.True(manager.TryResolveFrontendAsset(name, "web/main.js", out string? assetPath));
            Assert.NotNull(assetPath);
            Assert.False(manager.TryResolveFrontendAsset(name, "../plugin.json", out _));

            settings.PluginPreferences[name].Enabled = false;
            manager.LoadAll();
            Assert.Empty(manager.FrontendDescriptors);
        }
        finally
        {
            manager.ShutdownAll();
            ReleasePluginContexts();
            DeletePluginDirectory(root);
        }
    }

    private static string CreatePluginDirectory(string name, Type entryType, string apiVersion = "1.0", bool frontend = false)
    {
        string root = Path.Combine(AppPaths.PluginsDir, name);
        DeletePluginDirectory(root);
        DeletePluginState(name);
        Directory.CreateDirectory(root);
        string assemblyPath = typeof(NexusPipeline.TestPlugin.TestPlugin).Assembly.Location;
        File.Copy(assemblyPath, Path.Combine(root, Path.GetFileName(assemblyPath)), overwrite: true);
        if (frontend)
        {
            Directory.CreateDirectory(Path.Combine(root, "web"));
            File.WriteAllText(Path.Combine(root, "web", "main.js"), "export function activate() {}\n");
        }
        string capabilities = frontend ? "[\"background-jobs\", \"frontend-module\"]" : "[\"background-jobs\"]";
        string frontendSection = frontend
            ? ",\n  \"frontend\": {\n    \"apiVersion\": \"1.0\",\n    \"entry\": \"web/main.js\",\n    \"styles\": []\n  }"
            : "";
        string manifest = $$"""
        {
          "schemaVersion": 1,
          "name": "{{name}}",
          "displayName": "{{name}}",
          "description": "managed fixture",
          "version": "0.1.0",
          "kind": "managed-code",
          "apiVersion": "{{apiVersion}}",
          "entryAssembly": "{{Path.GetFileName(assemblyPath)}}",
          "entryType": "{{entryType.FullName}}",
          "capabilities": {{capabilities}}{{frontendSection}}
        }
        """;
        File.WriteAllText(Path.Combine(root, "plugin.json"), manifest);
        return root;
    }

    private static string PluginStatePath(string name)
    {
        return Path.Combine(AppPaths.ConfigDir, "plugins", name + ".json");
    }

    private static FixtureState? ReadState(string path)
    {
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<FixtureState>(File.ReadAllText(path), JsonOpts.Default);
    }

    private static string? ReadSecret(string name, string key)
    {
        string path = Path.Combine(AppPaths.ConfigDir, "plugins", name + ".secrets.json");
        if (!File.Exists(path)) return null;
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        string stored = doc.RootElement.GetProperty(key).GetString() ?? "";
        return SecretStore.TryDecrypt(stored, out string? plain) ? plain : null;
    }

    private static bool WaitUntil(Func<bool> predicate, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return true;
            Thread.Sleep(25);
        }
        return predicate();
    }

    private static void DeletePluginDirectory(string path)
    {
        for (int attempt = 0; attempt < 5 && Directory.Exists(path); attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                ReleasePluginContexts();
                Thread.Sleep(50);
            }
        }
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private static void DeletePluginState(string name)
    {
        string statePath = PluginStatePath(name);
        if (File.Exists(statePath)) File.Delete(statePath);

        string secretPath = Path.Combine(AppPaths.ConfigDir, "plugins", name + ".secrets.json");
        if (File.Exists(secretPath)) File.Delete(secretPath);
    }

    private static void ReleasePluginContexts()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static bool CanUseDpapi()
    {
        try
        {
            string encrypted = SecretStore.Encrypt("fixture-probe");
            return SecretStore.TryDecrypt(encrypted, out string? plain) && plain == "fixture-probe";
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    private sealed class FixtureSettingsProvider : ISettingsProvider
    {
        public AppSettings Current { get; } = new();
    }
}
