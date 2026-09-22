using System.Text.Json.Nodes;
using NexusPipeline.Modules.Configuration.Scripting;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Execution.Judgement;
using Xunit;

namespace NexusPipeline.Tests.Execution;

public sealed class TaskEnvironmentProbeTests
{
    [Fact]
    public void UndeclaredAndSpecialTargetsDoNotBecomeFilesystemQueries()
    {
        var check = new TaskEnvironmentCheckDescriptor(
            "game-target", "host", null, null, "gameTarget", "file", "none", false, false);
        var context = TaskExecutionContext.Unknown("user", "script", "preview") with
        {
            GameTarget = new("executable", "\\\\server\\share\\game.exe", null),
        };
        var probe = new TaskEnvironmentProbe([check], new TaskConfigView(), context, "", "");

        TaskEnvironmentInspection special = probe.Inspect("game-target");
        TaskEnvironmentInspection undeclared = probe.Inspect("not-declared");

        Assert.Equal("unsupported", special.Status);
        Assert.Equal("unsupported_target", special.Reason);
        Assert.Equal("not_checked", undeclared.Status);
        Assert.Equal("undeclared_inspection", undeclared.Reason);
    }

    [Fact]
    public void DeclaredConfigSelectorReturnsOnlyTargetMetadata()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-environment-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string target = Path.Combine(root, "game.exe");
            File.WriteAllText(target, "fixture");
            string config = Path.Combine(root, "config.json");
            File.WriteAllText(config, JsonSerializerOptionsJson(new { gamePath = target }));
            var view = new TaskConfigView();
            view.AddConfig("config:config.json", config, "json");
            var check = new TaskEnvironmentCheckDescriptor(
                "declared-target", "config", "config:config.json",
                JsonNode.Parse("[\"gamePath\"]")!.AsArray(), null, "file", "none", false, false);
            var context = TaskExecutionContext.Unknown("user", "script", "preview");
            var probe = new TaskEnvironmentProbe([check], view, context, root, "");

            TaskEnvironmentInspection result = probe.Inspect("declared-target");

            Assert.Equal("present", result.Status);
            Assert.Equal("file", result.ActualKind);
            Assert.Equal("target_present", result.Reason);
               Assert.DoesNotContain(target, result.Reason ?? string.Empty);
            Assert.Equal("not_checked", probe.Inspect("config:other").Status);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void DeclaredConfigTargetMatchesBoundExecutableWhenContextIsKnown()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-environment-probe-match-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string target = Path.Combine(root, "game.exe");
            File.WriteAllText(target, "fixture");
            string config = Path.Combine(root, "config.json");
            File.WriteAllText(config, JsonSerializerOptionsJson(new { gamePath = target }));
            var view = new TaskConfigView();
            view.AddConfig("config:config.json", config, "json");
            var check = new TaskEnvironmentCheckDescriptor(
                "declared-target", "config", "config:config.json",
                JsonNode.Parse("[\"gamePath\"]")!.AsArray(), null, "file", "none", false, false);
            var context = TaskExecutionContext.Unknown("user", "script", "preview") with
            {
                GameTarget = new("executable", target, null),
            };
            var probe = new TaskEnvironmentProbe([check], view, context, root, "");

            TaskEnvironmentInspection result = probe.Inspect("declared-target");

            Assert.Equal("present", result.Status);
            Assert.Equal("file", result.ActualKind);
            Assert.True(result.MatchesContext);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void WrongKindIsReportedWithoutTreatingDirectoryAsFile()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-environment-probe-kind-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var check = new TaskEnvironmentCheckDescriptor(
                "game-target", "host", null, null, "gameTarget", "file", "none", false, false);
            var context = TaskExecutionContext.Unknown("user", "script", "preview") with
            {
                GameTarget = new("executable", root, null),
            };
            var probe = new TaskEnvironmentProbe([check], new TaskConfigView(), context, root, "");

            TaskEnvironmentInspection result = probe.Inspect("game-target");

            Assert.Equal("wrong_kind", result.Status);
            Assert.Equal("directory", result.ActualKind);
               Assert.True(result.MatchesContext == true);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RelativeConfigTargetCannotEscapeTheDeclaredConfigDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-environment-probe-relative-" + Guid.NewGuid().ToString("N"));
        string configDirectory = Path.Combine(root, "config");
        Directory.CreateDirectory(configDirectory);
        try
        {
            string config = Path.Combine(configDirectory, "config.json");
            File.WriteAllText(config, "{\"gamePath\":\"..\\\\outside.exe\"}");
            var view = new TaskConfigView();
            view.AddConfig("config:config.json", config, "json");
            var check = new TaskEnvironmentCheckDescriptor(
                "declared-target", "config", "config:config.json",
                JsonNode.Parse("[\"gamePath\"]")!.AsArray(), null, "file", "config_directory", false, false);
            var probe = new TaskEnvironmentProbe([check], view,
                TaskExecutionContext.Unknown("user", "script", "preview"), root, "");

            TaskEnvironmentInspection result = probe.Inspect("declared-target");

            Assert.Equal("not_checked", result.Status);
            Assert.Equal("target_not_resolvable", result.Reason);
        }
        finally { Directory.Delete(root, true); }
    }

    private static string JsonSerializerOptionsJson(object value) =>
        System.Text.Json.JsonSerializer.Serialize(value);
}
