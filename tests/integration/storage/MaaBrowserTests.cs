using System.Text.Json;
using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Extensions;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class MaaBrowserTests
{
    [Fact(DisplayName = "The Installed Maa Editor Saves An Initialization Choice And The Actual Dispatch Confirmation Controls Its Single Execution")]
    public async Task InitializesInstalledProject()
    {
        using var driver = new BuiltExtensionInputs("maa-framework", "NEXUS_PACKAGED_MAA_RECEIPT");
        using var consumer = new BuiltExtensionInputs("maas", "NEXUS_PACKAGED_MAAS_RECEIPT");
        ProjectSourceFixture? source = null; object? context = null; byte[]? original = null;
        await FrontendBrowserTests.RunAsync(driver, "NEXUS_MAA_INITIALIZATION_BROWSER_SCRIPT", 3, denyNetwork: true, remoteAccess: true,
            additional: [consumer], seed: catalog =>
            {
                source = new(configuration: true, file: true, enabled: true, catalog: catalog, configurationName: "mxu.json");
                MaaInitializationSource.CopyProgram(source.Path);
                Seed(source);
                var marker = Path.Combine(source.Path, "browser-initialization");
                MaaInitializationSource.Prepare(source, driver.Archive, marker, false, false);
                original = File.ReadAllBytes(source.ConfigurationFile!);
                var definitions = new AutomationDefinitionStore(catalog.Database).Read();
                var binding = definitions.Bindings.Single(item => item.Id == source.Binding);
                context = new { bindingId = source.Binding, marker,
                    userName = definitions.Users.Single(item => item.Id == binding.UserId).Name };
            }, browserContext: () => context, timeoutSeconds: 240);
        Assert.NotNull(source);
        Assert.Equal(original, File.ReadAllBytes(source.ConfigurationFile!));
        source.Dispose();
    }

    [Fact(DisplayName = "The Original Maa And MaaS Packages Mount The Full Binding Editor In The Actual Authenticated App And Save Host Owned Drafts Without Writing Upstream")]
    public async Task EditsInstalledProject()
    {
        using var driver = new BuiltExtensionInputs("maa-framework", "NEXUS_PACKAGED_MAA_RECEIPT");
        using var consumer = new BuiltExtensionInputs("maas", "NEXUS_PACKAGED_MAAS_RECEIPT");
        ProjectSourceFixture? source = null;
        object? context = null;
        byte[]? original = null;
        // The case opens, previews and saves three times, and every preview recompiles the project through the Host,
        // so its five compile rounds do not fit the shared 90 s script budget.
        await FrontendBrowserTests.RunAsync(driver, "NEXUS_MAA_BROWSER_SCRIPT", 8, denyNetwork: true, remoteAccess: true, additional: [consumer], seed: catalog =>
        {
            source = new(configuration: true, file: true, catalog: catalog, configurationName: "mxu.json");
            Seed(source);
            original = File.ReadAllBytes(source.ConfigurationFile!);
            var definitions = new AutomationDefinitionStore(catalog.Database).Read();
            var binding = definitions.Bindings.Single(item => item.Id == source.Binding);
            context = new { bindingId = source.Binding, userName = definitions.Users.Single(item => item.Id == binding.UserId).Name };
        }, browserContext: () => context, timeoutSeconds: 240);
        Assert.NotNull(source);
        Assert.Equal(original, File.ReadAllBytes(source.ConfigurationFile!));
        source.Dispose();
    }

    [Fact(DisplayName = "The Installed Maa Script Editor Reads Its Own Sources Saves Independent Choices And Reopens Them Without Changing Binding Configuration")]
    public async Task EditsInstalledScript()
    {
        using var driver = new BuiltExtensionInputs("maa-framework", "NEXUS_PACKAGED_MAA_RECEIPT");
        using var consumer = new BuiltExtensionInputs("maas", "NEXUS_PACKAGED_MAAS_RECEIPT");
        ProjectSourceFixture? source = null; object? context = null; byte[]? original = null;
        await FrontendBrowserTests.RunAsync(driver, "NEXUS_MAA_SCRIPT_BROWSER_SCRIPT", 4, denyNetwork: true, remoteAccess: true,
            additional: [consumer], seed: catalog =>
            {
                source = new(configuration: true, file: true, catalog: catalog, configurationName: "mxu.json");
                Seed(source); original = File.ReadAllBytes(source.ConfigurationFile!);
                var definitions = new AutomationDefinitionStore(catalog.Database).Read();
                var binding = definitions.Bindings.Single(item => item.Id == source.Binding);
                context = new { bindingId = source.Binding, scriptId = binding.ScriptId };
            }, browserContext: () => context, timeoutSeconds: 240);
        Assert.NotNull(source);
        Assert.Equal(original, File.ReadAllBytes(source.ConfigurationFile!));
        source.Dispose();
    }

    internal static void Seed(ProjectSourceFixture source)
    {
        var assets = Path.Combine(source.Path, "assets"); Directory.CreateDirectory(Path.Combine(assets, "resource", "pipeline"));
        File.WriteAllBytes(Path.Combine(assets, "interface.json"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            interface_version = 2, name = "Maa browser project", version = "1.0",
            controller = new[] { new { name = "PC", type = "Win32", win32 = new { screencap = "PrintWindow", mouse = "SendMessage", keyboard = "SendMessage" } } },
            resource = new[] { new { name = "Official", path = new[] { "resource" } } },
            task = new[] { new { name = "Daily", entry = "DailyEntry", default_check = true, repeatable = true, option = new[] { "Mode" } } },
            option = new Dictionary<string, object>
            {
                ["Mode"] = new { type = "select", default_case = "A", cases = new[] { new { name = "A" }, new { name = "B" } } },
                ["Login"] = new { type = "input", label = "Credentials", inputs = new[] { new { name = "user" } } }
            },
            global_option = new[] { "Login" },
            preset = new[] { new { name = "DailyOnly", label = "Daily only", task = new[] { new { name = "Daily", enabled = true } } } }
        }));
        File.WriteAllText(Path.Combine(assets, "resource", "pipeline", "main.json"), "{\"DailyEntry\":{\"recognition\":\"DirectHit\",\"action\":\"DoNothing\"}}");
        // MXU keeps global_option values at the document root (MxuConfig.globalOptionValues); instances carry no such member.
        File.WriteAllBytes(source.ConfigurationFile!, JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = "1.0", instances = new[] { new { id = "one", name = "Browser account", controllerName = "PC", resourceName = "Official",
                tasks = new[] { new { id = "first", taskName = "Daily", enabled = true, enabledByController = new { PC = true }, optionValues = new { } } } } },
            globalOptionValues = new { Login = new { type = "input", values = new { user = "original-user" } } }
        }));
        var script = new AutomationDefinitionStore(source.Catalog.Database).Read().Scripts[0];
        var tree = new FileTreeArchive(new(SnapshotBudget.Default), new(source.Catalog.Instance, new())).Capture(source.ConfigurationFile!, default).Snapshot;
        source.Save(new(source.Address, 1, 1, AdapterConfigurationPolicy.Contract(script, script.AdapterResources[0]), tree, ConfigurationUpdateKind.Manual));
    }
}
