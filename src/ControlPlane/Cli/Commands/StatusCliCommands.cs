using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.ControlPlane.Cli;

namespace NexusPipeline.ControlPlane.Cli.Commands;

internal static partial class CliCommandRouter
{
    private static int ExecuteStatus(CliArguments args)
    {
        if (!EnsurePositionals(args, 1, CliText.Get("error.extra_arguments", "{usage} 不接受额外参数", ("usage", "status")))
            || !EnsureOptions(args))
        {
            return CliExitCodes.For("invalid_arguments");
        }
        return ReturnApi(new CliApiClient().Get("/api/status"));
    }

    private static int ExecuteDoctor(CliArguments args)
    {
        string? sub = Positional(args, 1)?.ToLowerInvariant();
        var client = new CliApiClient();
        if (sub is null)
        {
            return EnsurePositionals(args, 1, CliText.Get("error.extra_arguments", "{usage} 不接受额外参数", ("usage", "doctor")))
                && EnsureOptions(args)
                ? ReturnApi(client.Get("/api/diagnostics"))
                : CliExitCodes.For("invalid_arguments");
        }
        if (!sub.Equals("export", StringComparison.OrdinalIgnoreCase))
        {
            return CliOutput.WriteFailure(
                "invalid_arguments",
                CliText.Get("error.unknown_subcommand", "未知 {command} 子命令：{subcommand}",
                    ("command", "doctor"),
                    ("subcommand", sub)));
        }
        if (!EnsurePositionals(args, 2, CliText.Get("error.extra_arguments", "{usage} 不接受额外参数", ("usage", "doctor export")))
            || !EnsureOptions(args, "output"))
        {
            return CliExitCodes.For("invalid_arguments");
        }
        string? destination = args.Get("output");
        try
        {
            if (destination is not null) destination = CliArtifactOutput.ValidateDestination(destination);
            CliApiResponse response = client.Post("/api/diagnostics/export");
            if (response.Succeeded && destination is not null)
                response = response with { Body = client.CopyDiagnosticArtifact(response.Body ?? throw new InvalidDataException("诊断包回执缺失"), destination) };
            return ReturnApi(response, CliText.Get("success.diagnostics_exported", "诊断包已导出"));
        }
        catch (UnauthorizedAccessException)
        { return CliOutput.WriteFailure("operation_forbidden", "无权写入诊断包输出目标"); }
        catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException)
        { return CliOutput.WriteFailure("validation_error", exception.Message); }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        { return CliOutput.WriteFailure("diagnostics_export_failed", "诊断包下载失败"); }
    }

}
