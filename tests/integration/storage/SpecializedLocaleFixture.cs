using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Configuration;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal static class SpecializedLocaleFixture
{
    internal static object Seed(ExtensionCatalogFixture catalog, JsonElement fixtures)
    {
        var worker = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE") ?? throw new IOException("fixture_worker_required");
        var roots = new List<AutomationRoot>(); var scripts = new List<AutomationScript>();
        var users = new List<AutomationUser>(); var bindings = new List<AutomationBinding>(); var contexts = new List<object>();
        foreach (var fixture in fixtures.GetProperty("cases").EnumerateArray())
        {
            var extension = fixture.GetProperty("extensionId").GetString()!;
            var directory = Path.Combine(Path.GetDirectoryName(catalog.Instance.RuntimeRoot)!, "localized-" + extension);
            Directory.CreateDirectory(directory);
            CopyWorker(worker, directory);
            foreach (var source in fixture.GetProperty("sourceFiles").EnumerateObject())
            {
                var path = SafePath(directory, source.Name); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, source.Value.GetRawText());
            }
            var root = new AutomationRoot(Guid.NewGuid(), 0, "Controlled " + extension, AutomationRootKind.Script, directory);
            roots.Add(root);
            var resources = fixture.GetProperty("resources").EnumerateArray().ToArray();
            var configurationPaths = new List<AutomationPath>(); var declarations = new List<AutomationAdapterResource>();
            foreach (var resource in resources)
            {
                var relative = resource.GetProperty("sourcePath").GetString()!;
                var path = SafePath(directory, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, resource.GetProperty("content").GetString());
                configurationPaths.Add(new(root.Id, relative));
                var access = ImmutableArray.Create(new ConfigurationAccessGrant(new([]), true));
                declarations.Add(new(resource.GetProperty("resourceId").GetString()!, declarations.Count, "", 1,
                    ConfigurationSyntax.Json, new(access, access, [])));
            }
            var script = new AutomationScript(Guid.NewGuid(), 0, "Locale " + extension, AutomationKind.Specialized, root.Id, extension,
                new(root.Id, Path.GetFileName(worker)), ["account-task-fixture", "succeeded", Path.Combine(directory, "controlled-run")],
                [.. configurationPaths], [], new(1, 0, 60), new(AutomationJudgementLanguage.Keywords, [], [], null))
                { AdapterResources = [.. declarations] };
            var user = new AutomationUser(Guid.NewGuid(), 0, "Locale " + extension, users.Count);
            var binding = new AutomationBinding(Guid.NewGuid(), 0, user.Id, script.Id, true, 0) { NotifyEnabled = false };
            scripts.Add(script); users.Add(user); bindings.Add(binding);
            contexts.Add(new { extensionId = extension, bindingId = binding.Id, marker = Path.Combine(directory, "controlled-run"),
                resources = resources.Select(resource => resource.GetProperty("resourceId").GetString()),
                tasks = fixture.GetProperty("tasks").Clone(), diagnostics = fixture.GetProperty("diagnostics").Clone(),
                files = configurationPaths.Select(path => new { path = SafePath(directory, path.RelativePath),
                    content = File.ReadAllText(SafePath(directory, path.RelativePath)) }).ToArray() });
        }
        var changes = new AutomationChanges(0, [.. roots], [.. scripts], [.. users], [.. bindings], [], [], []);
        var store = new AutomationDefinitionStore(catalog.Database);
        store.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "specialized-locale-fixture", changes), changes, DateTimeOffset.UtcNow);
        var saved = store.Read(); var archive = new FileTreeArchive(new(SnapshotBudget.Default), new(catalog.Instance, new()));
        var revisions = new ConfigurationRevisionStore(catalog.Instance, catalog.Database, new());
        foreach (var binding in saved.Bindings)
        {
            var script = saved.Scripts.Single(item => item.Id == binding.ScriptId);
            foreach (var declaration in script.AdapterResources)
            {
                var path = script.ConfigurationPaths[declaration.DeclarationIndex];
                var directory = saved.Roots.Single(root => root.Id == path.RootId).Path;
                var tree = archive.Capture(SafePath(directory, path.RelativePath), default).Snapshot;
                var change = new ConfigurationRevisionChange(new(binding.Id, declaration.ResourceId), 0, binding.Revision,
                    AdapterConfigurationPolicy.Contract(script, declaration), tree, ConfigurationUpdateKind.Manual);
                revisions.Commit(ConfigurationRevisionStore.Identity(Guid.NewGuid(), "specialized-locale-fixture", change), change, DateTimeOffset.UtcNow);
            }
        }
        return new { cases = contexts };
    }

    private static string SafePath(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("locale_fixture_path_invalid");
        return path;
    }

    private static void CopyWorker(string executable, string destination)
    {
        var root = Path.GetDirectoryName(executable)!;
        foreach (var source in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var target = SafePath(destination, Path.GetRelativePath(root, source));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
        }
    }
}
