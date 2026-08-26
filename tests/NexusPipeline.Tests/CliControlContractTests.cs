using NexusPipeline.App;
using NexusPipeline.App.Contracts;
using NexusPipeline.Cli;
using NexusPipeline.Web;
using Xunit;
using System.Text.Json.Nodes;

namespace NexusPipeline.Tests;

public sealed class CliControlContractTests
{
    [Fact]
    public void TargetResolver_prefers_exact_id_before_name()
    {
        var targets = new[]
        {
            new Target("abc", "same"),
            new Target("def", "same"),
        };

        TargetResolution<Target> result = TargetResolver.Resolve(
            targets,
            "ABC",
            target => target.Id,
            target => target.Name);

        Assert.True(result.IsFound);
        Assert.Equal("abc", result.Value?.Id);
    }

    [Fact]
    public void TargetResolver_returns_ambiguous_for_duplicate_names()
    {
        var targets = new[]
        {
            new Target("abc", "same"),
            new Target("def", "same"),
        };

        TargetResolution<Target> result = TargetResolver.Resolve(
            targets,
            " SAME ",
            target => target.Id,
            target => target.Name);

        Assert.Equal(TargetResolutionKind.Ambiguous, result.Kind);
        Assert.Null(result.Value);
        Assert.Equal(new[] { "abc", "def" }, result.Candidates.Select(target => target.Id));
    }

    [Fact]
    public void CliArguments_supports_json_files_stdin_and_legacy_flags()
    {
        bool parsed = CliArguments.TryParse(
            new[] { "script", "create", "--json", "--file", "-", "-Auto", "--name=value" },
            out CliArguments? args,
            out string? error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.NotNull(args);
        Assert.Equal(new[] { "script", "create" }, args!.Positionals);
        Assert.True(args.Has("json") == false);
        Assert.Equal("-", args.Get("file"));
        Assert.Equal("value", args.Get("name"));
        Assert.True(args.Has("auto"));
    }

    [Fact]
    public void CliExitCodes_are_stable_for_machine_contract()
    {
        Assert.Equal(0, CliExitCodes.For("ok"));
        Assert.Equal(2, CliExitCodes.For("validation_error"));
        Assert.Equal(3, CliExitCodes.For("ambiguous_target"));
        Assert.Equal(4, CliExitCodes.For("resource_busy"));
        Assert.Equal(5, CliExitCodes.For("service_unavailable"));
        Assert.Equal(6, CliExitCodes.For("operation_forbidden"));
        Assert.Equal(7, CliExitCodes.For("execution_failed"));
        Assert.Equal(8, CliExitCodes.For("cancelled"));
        Assert.Equal(9, CliExitCodes.For("internal_error"));
    }

    [Fact]
    public void Lightweight_control_plane_serves_api_without_web_ui_or_remote_binding()
    {
        WebServerOptions options = WebServerOptions.FromSettings(lightweight: true, allowRemoteAccess: true);

        Assert.False(options.ServeWebUi);
        Assert.False(options.AllowRemoteAccess);
    }

    [Fact]
    public void OperationResult_preserves_error_code_kind_and_candidates()
    {
        OperationResult<string> result = OperationResult<string>.Failure(
            "ambiguous_target",
            "目标不唯一",
            OperationErrorKind.Conflict,
            new[] { "first", "second" });

        Assert.False(result.Succeeded);
        Assert.Equal("ambiguous_target", result.ErrorCode);
        Assert.Equal(OperationErrorKind.Conflict, result.ErrorKind);
        Assert.Equal(new[] { "first", "second" }, result.Error?.Candidates);
    }

    [Fact]
    public void CliOutput_machine_success_and_failure_are_single_json_envelopes()
    {
        TextWriter previous = Console.Out;
        using var output = new StringWriter();
        Console.SetOut(output);
        try
        {
            CliOutput.Configure(new[] { "--json" });
            CliOutput.WriteSuccess(new JsonObject { ["value"] = "ok" });
            JsonObject success = JsonNode.Parse(output.ToString())!.AsObject();

            Assert.True(success["ok"]!.GetValue<bool>());
            Assert.Equal("ok", success["code"]!.ToString());
            Assert.Equal("ok", success["data"]!["value"]!.ToString());

            output.GetStringBuilder().Clear();
            int exitCode = CliOutput.WriteFailure("not_found", "目标不存在");
            JsonObject failure = JsonNode.Parse(output.ToString())!.AsObject();

            Assert.Equal(3, exitCode);
            Assert.False(failure["ok"]!.GetValue<bool>());
            Assert.Equal("not_found", failure["code"]!.ToString());
            Assert.Equal("目标不存在", failure["message"]!.ToString());
        }
        finally
        {
            Console.SetOut(previous);
            CliOutput.Configure(Array.Empty<string>());
        }
    }

    [Fact]
    public void Cli_and_web_adapters_do_not_write_runtime_persistence_directly()
    {
        string root = FindProjectRoot();
        string[] adapterDirectories =
        {
            Path.Combine(root, "src", "Cli"),
            Path.Combine(root, "src", "Web"),
        };
        string[] forbidden = { "DataStore.Save", "ConfigStore.Save" };

        foreach (string directory in adapterDirectories)
        {
            foreach (string file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.TopDirectoryOnly))
            {
                string contents = File.ReadAllText(file);
                foreach (string marker in forbidden)
                {
                    Assert.DoesNotContain(marker, contents);
                }
            }
        }

        string scriptsHandler = File.ReadAllText(
            Path.Combine(root, "src", "Web", "ApiScriptsHandler.cs"));
        foreach (string marker in new[]
        {
            "TryBeginEditSession",
            "UserConfigManager.EditSessions[",
            "UserConfigManager.PrepareForEdit(",
            "UserConfigManager.CommitEdit(",
            "UserConfigManager.CancelEdit(",
            "SystemActions.StartVisible(",
        })
        {
            Assert.DoesNotContain(marker, scriptsHandler);
        }
    }

    private static string FindProjectRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "src", "NexusPipeline.csproj")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new InvalidOperationException("无法定位 NexusPipeline 项目根目录");
    }

    private sealed record Target(string Id, string Name);
}
