using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Extensions;

namespace NexusPipeline.StorageTests;

internal static class MaaRetrySource
{
    internal static void Prepare(ProjectSourceFixture source, string extension, string nativeArchive, string marker, bool repeated = false, bool pcStartup = false)
    {
        MaaWorkerSource.Prepare(source, false, marker, agent: true, preparation: pcStartup);
        MaaInitializationSource.PrepareProgram(source, nativeArchive, agent: true);
        var definitions = new AutomationDefinitionStore(source.Catalog.Database);
        var before = definitions.Read(); var script = before.Scripts[0] with { Limits = new(2, 300, 720) };
        script = script with { Target = script.Target! with {
            Pc = script.Target!.Pc! with { Arguments = ["capture-window-fixture", marker, "retry-static-frame"] } } };
        if (extension == "maas") script = script with {
            ConfigurationPaths = script.ConfigurationPaths.Add(new(script.RootId, "appsettings.json")).Add(new(script.RootId, "config/config.json")),
            AdapterResources = script.AdapterResources.Add(new("settings", 1, "", 1, ConfigurationSyntax.Json, new([], [new(new([]), true)], [])))
                .Add(new("global", 2, "", 1, ConfigurationSyntax.Json, new([], [new(new([]), true)], []))) };
        var changes = new AutomationChanges(before.Revision, [], [script], [], [], [], [], []);
        definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "maa-retry", changes), changes, DateTimeOffset.UtcNow);
        var names = extension == "maaend" ? new[] { "LaunchGameLinux", "DailyRewards", "DijiangRewards", "VisitFriends" }
            : ["登录游戏", "领取每日奖励", "领取任务奖励", "领取邮箱奖励"];
        var entries = extension == "maaend" ? new[] { "GameLauncherMain", "DailyRewardStart", "DijiangRewards", "VisitFriendsMain" }
            : ["登录_登录", "采购_入口", "任务_入口", "邮箱_入口"];
        if (pcStartup) names[0] = "__MXU_PRETASK__LaunchGamePC";
        string[] roles = ["login", "completed", "failed", "pending"];
        var path = Path.Combine(source.Path, "assets/interface.json");
        var pi = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        pi["name"] = extension == "maaend" ? "MaaEnd" : "MaaStellaSora";
        pi["task"] = new JsonArray(names.Select((name, index) => (JsonNode)new JsonObject {
            ["name"] = name, ["entry"] = entries[index], ["default_check"] = true, ["repeatable"] = true }).ToArray());
        pi["agent"] = new JsonObject { ["child_exec"] = "../init/run.exe",
            ["child_args"] = new JsonArray("maa-agent-fixture", marker + ".agent") };
        if (pcStartup)
        {
            pi["task"]!.AsArray().RemoveAt(0);
            pi["pretask"] = new JsonObject { ["name"] = "LaunchGamePC", ["exec"] = "agent/go-service",
                ["args"] = new JsonArray("--pretask", "LaunchGamePC") };
            pi["option"] = new JsonObject { ["LaunchGamePCGameSetting"] = new JsonObject { ["type"] = "select", ["default_case"] = "No",
                ["cases"] = new JsonArray(new JsonObject { ["name"] = "No" }, new JsonObject { ["name"] = "Yes" }) } };
            File.WriteAllBytes(Path.Combine(source.Path, "init/maa-startup-fixture.json"),
                JsonSerializer.SerializeToUtf8Bytes(new { executable = Path.Combine(source.Path, "run.exe"), marker }));
        }
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(pi));
        var pipeline = new JsonObject();
        for (var index = 0; index < entries.Length; index++) pipeline[entries[index]] = new JsonObject {
            ["recognition"] = "DirectHit", ["action"] = "Custom", ["custom_action"] = "OwnedAgentAction",
            ["custom_action_param"] = new JsonObject { ["text"] = "中文 Agent", ["retryTask"] = roles[index] },
            ["pre_delay"] = 0, ["post_delay"] = 0 };
        if (repeated) pipeline[entries[2]]!["custom_action_param"]!["failAtCall"] = 2;
        File.WriteAllBytes(Path.Combine(source.Path, "assets/resource/pipeline/main.json"), JsonSerializer.SerializeToUtf8Bytes(pipeline));
        object configuration = extension == "maaend" ? new { version = "1.0", instances = new[] {
            new { id = "one", controllerName = "PC", resourceName = "Official", tasks = names.Select((name, index) => new {
                id = roles[index], taskName = name, enabled = true, enabledByController = new { PC = true }, optionValues = new { } }).ToArray() } } }
            : new { CurrentControllerName = "PC", Resource = "Official", BeforeTask = "None", AfterTask = "None",
                TaskItems = names.Select((name, index) => new { name, entry = entries[index], default_check = true, repeat_count = repeated && index == 2 ? 3 : 1,
                    option = Array.Empty<object>() }).ToArray(), CurrentTasks = names.Select((name, index) => name + "<|||>" + entries[index]).ToArray() };
        File.WriteAllBytes(source.ConfigurationFile!, JsonSerializer.SerializeToUtf8Bytes(configuration));
        if (extension == "maas")
        {
            File.WriteAllText(Path.Combine(source.Path, "appsettings.json"), "{}");
            File.WriteAllText(Path.Combine(source.Path, "config/config.json"), "{}");
        }
        var tree = new FileTreeArchive(new(SnapshotBudget.Default), new(source.Catalog.Instance, new())).Capture(source.ConfigurationFile!, default).Snapshot;
        source.Save(new(source.Address, 2, 1, AdapterConfigurationPolicy.Contract(script, script.AdapterResources[0]), tree, ConfigurationUpdateKind.Manual));
        if (extension == "maas") SaveShared(source, script);
    }

    private static void SaveShared(ProjectSourceFixture source, AutomationScript script)
    {
        foreach (var resource in script.AdapterResources.Skip(1))
        {
            var relative = resource.ResourceId == "settings" ? "appsettings.json" : "config/config.json";
            var tree = new FileTreeArchive(new(SnapshotBudget.Default), new(source.Catalog.Instance, new())).Capture(Path.Combine(source.Path, relative), default).Snapshot;
            source.Save(new(new(source.Binding, resource.ResourceId), 0, 1, AdapterConfigurationPolicy.Contract(script, resource), tree, ConfigurationUpdateKind.Manual));
        }
    }

    internal static JsonElement Choice(string extension, bool pcStartup = false) => JsonSerializer.SerializeToElement(new {
        InterfacePath = "assets/interface.json", ClientKind = extension == "maaend" ? "mxu" : "mfa", Language = "zh_cn",
        Runtime = new { NativeDirectory = "native", Executables = pcStartup ? new Dictionary<string, string> { ["agent/go-service"] = "init/run.exe" }
            : new Dictionary<string, string>(), Directories = Array.Empty<string>() },
        Source = new { Kind = extension == "maaend" ? "mxu" : "mfa", Path = extension == "maaend" ? "local.json" : "config/instances/one.json", InstanceId = "one" } });
}
