using System.Text.Json;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.TestPlugin;
using Xunit;
using NexusPipeline.ControlPlane.Http;
using NexusPipeline.Modules.Notifications;
using NexusPipeline.Modules.Plugins.Managed;
using NexusPipeline.Modules.Plugins.Runtime;
using NexusPipeline.Modules.Plugins;
using NexusPipeline.Modules.Settings.Contracts;
using NexusPipeline.Modules.Settings;
using NexusPipeline.Platform.Security;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Shared.Serialization;

namespace NexusPipeline.Tests.Plugins;

public sealed class ManagedPluginTests
{
    [Fact]
    public void ManagedPlugin_DefaultDisabled_EnableAfterReload_AndHostServicesWork()
    {
        string root = CreatePluginDirectory("fixture-managed", typeof(NexusPipeline.TestPlugin.TestPlugin), apiVersion: "1.6");
        var settings = new AppSettings();
        var manager = new PluginManager(
            new TestSettingsProvider(settings),
            new TestPluginNotificationSink(),
            new AllowAllPluginConfigurationMutationGate());

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
            new TestSettingsProvider(settings),
            new TestPluginNotificationSink(),
            new AllowAllPluginConfigurationMutationGate());

        try
        {
            manager.LoadAll();
            Assert.Equal("Incompatible", manager.GetRuntimeState("fixture-incompatible"));
            Assert.Contains("API", manager.GetRuntimeError("fixture-incompatible"));
            Assert.Equal("plugin_incompatible_api", manager.GetRuntimeErrorCode("fixture-incompatible"));
            Assert.False(File.Exists(PluginStatePath("fixture-incompatible")));

            Assert.Equal("InitFailed", manager.GetRuntimeState("fixture-failing"));
            Assert.Contains("fixture init failure", manager.GetRuntimeError("fixture-failing"));
            Assert.False(manager.IsEnabled("fixture-failing"));
            Assert.Empty(manager.GetEmulatorSupportProviders());
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
    public void EmulatorSupportAdapter_DisposeRevokesRegistrationAndRejectsLateRegistration()
    {
        var registry = new PluginEmulatorSupportRegistry();
        var adapter = new PluginEmulatorSupportAdapter(registry, "fixture-emulator");
        adapter.Register(new FixtureProvider());

        Assert.Single(registry.Snapshot(_ => true));

        adapter.Dispose();

        Assert.Empty(registry.Snapshot(_ => true));
        Assert.Throws<ObjectDisposedException>(() => adapter.Register(new FixtureProvider()));
    }

    [Fact]
    public async Task EmulatorSupportAdapter_DisposeRacingWithRegisterRevokesLateToken()
    {
        var registry = new PluginEmulatorSupportRegistry();
        var adapter = new PluginEmulatorSupportAdapter(registry, "fixture-emulator");
        using var getterEntered = new ManualResetEventSlim();
        using var releaseGetter = new ManualResetEventSlim();
        Task<ObjectDisposedException> register = Task.Run(() => Assert.Throws<ObjectDisposedException>(
            () => adapter.Register(new BlockingFixtureProvider(getterEntered, releaseGetter))));

        try
        {
            Assert.True(getterEntered.Wait(TimeSpan.FromSeconds(3)));
            adapter.Dispose();
        }
        finally
        {
            releaseGetter.Set();
        }

        await register;
        Assert.Empty(registry.Snapshot(_ => true));
    }

    private sealed class FixtureProvider : IPluginEmulatorSupportProvider
    {
        public string Id => "fixture-provider";

        public int Priority => 0;

        public ValueTask<PluginEmulatorProbeResult> ProbeAsync(
            string adbEndpoint,
            CancellationToken cancellationToken,
            int timeoutSeconds) =>
            ValueTask.FromResult(PluginEmulatorProbeResult.NotApplicable());
    }

    private sealed class BlockingFixtureProvider(
        ManualResetEventSlim getterEntered,
        ManualResetEventSlim releaseGetter) : IPluginEmulatorSupportProvider
    {
        public string Id
        {
            get
            {
                getterEntered.Set();
                if (!releaseGetter.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("fixture provider getter was not released");
                }
                return "late-provider";
            }
        }

        public int Priority => 0;

        public ValueTask<PluginEmulatorProbeResult> ProbeAsync(
            string adbEndpoint,
            CancellationToken cancellationToken,
            int timeoutSeconds) =>
            ValueTask.FromResult(PluginEmulatorProbeResult.NotApplicable());
    }

    [Fact]
    public void ManagedPlugin_MinimumHostVersionBlocksManualInstallationBeforeAssemblyLoad()
    {
        const string name = "fixture-host-too-old";
        string root = CreatePluginDirectory(
            name,
            typeof(NexusPipeline.TestPlugin.TestPlugin),
            minHostVersion: "99.0.0");
        var settings = new AppSettings
        {
            PluginPreferences = new Dictionary<string, PluginPreference>(StringComparer.OrdinalIgnoreCase)
            {
                [name] = new PluginPreference { Enabled = true },
            },
        };
        var manager = new PluginManager(
            new TestSettingsProvider(settings),
            new TestPluginNotificationSink(),
            new AllowAllPluginConfigurationMutationGate());

        try
        {
            manager.LoadAll();

            Assert.Equal("Incompatible", manager.GetRuntimeState(name));
            Assert.False(manager.IsEnabled(name));
            Assert.Contains("需要宿主", manager.GetRuntimeError(name));
            Assert.Equal("plugin_incompatible_host", manager.GetRuntimeErrorCode(name));
            Assert.Equal("99.0.0", manager.PluginSummaries.Single(item => item.Name == name).MinHostVersion);
            Assert.False(File.Exists(PluginStatePath(name)));
        }
        finally
        {
            manager.ShutdownAll();
            ReleasePluginContexts();
            DeletePluginDirectory(root);
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
            new TestSettingsProvider(settings),
            new TestPluginNotificationSink(),
            new AllowAllPluginConfigurationMutationGate());

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

    private static string CreatePluginDirectory(
        string name,
        Type entryType,
        string apiVersion = "1.0",
        bool frontend = false,
        string minHostVersion = "0.0.0")
    {
        string artifactName = char.ToUpperInvariant(name[0])
            + name[1..].Replace("-", "", StringComparison.Ordinal);
        string root = Path.Combine(AppPaths.PluginsDir, artifactName);
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
            ? ",\n  \"frontend\": {\n    \"apiVersion\": \"1.5\",\n    \"entry\": \"web/main.js\",\n    \"styles\": []\n  }"
            : "";
        string manifest = $$"""
        {
          "schemaVersion": 2,
          "name": "{{name}}",
          "artifactName": "{{artifactName}}",
          "displayName": "{{name}}",
          "description": "managed fixture",
          "version": "0.1.0",
          "kind": "managed-code",
          "minHostVersion": "{{minHostVersion}}",
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
