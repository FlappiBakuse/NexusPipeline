using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Localization;
using NexusPipeline.Utilities;

namespace NexusPipeline.Cli;

/// <summary>CLI 输出适配器：machine mode 下 stdout 只写稳定 JSON envelope。</summary>
internal static class CliOutput
{
    public static bool MachineMode { get; private set; }

    public static void Configure(IEnumerable<string> args)
    {
        MachineMode = args.Any(argument => string.Equals(argument, "--json", StringComparison.OrdinalIgnoreCase));
        Logger.ConsoleOutputToError = MachineMode;
    }

    public static void WriteSuccess(JsonNode? data, string? humanMessage = null)
    {
        if (MachineMode)
        {
            var envelope = new JsonObject
            {
                ["ok"] = true,
                ["code"] = "ok",
                ["data"] = data?.DeepClone() ?? new JsonObject(),
            };
            Console.WriteLine(envelope.ToJsonString(JsonOpts.Web));
            return;
        }

        if (!string.IsNullOrWhiteSpace(humanMessage))
        {
            Console.WriteLine(CliText.Get("output.done", "[完成] {message}", ("message", humanMessage)));
        }
        if (data is not null)
        {
            Console.WriteLine(data.ToJsonString(JsonOpts.Indented));
        }
    }

    public static int WriteFailure(string code, string message, JsonNode? data = null)
    {
        int exitCode = CliExitCodes.For(code);
        string localizedMessage = HostLocalization.TranslateUserMessage(code, message, LocaleCatalog.HostLocale);
        if (MachineMode)
        {
            var envelope = new JsonObject
            {
                ["ok"] = false,
                ["code"] = code,
                ["message"] = localizedMessage,
            };
            if (data is not null)
            {
                envelope["data"] = data.DeepClone();
            }
            Console.WriteLine(envelope.ToJsonString(JsonOpts.Web));
        }
        else
        {
            Console.WriteLine(CliText.Get("output.error", "[错误] {message}", ("message", localizedMessage)));
        }
        return exitCode;
    }

    public static void WriteDiagnostic(string message)
    {
        if (MachineMode)
        {
            Console.Error.WriteLine(HostLocalization.TranslateCli("diagnostic", message, LocaleCatalog.HostLocale));
        }
        else
        {
            Console.WriteLine(HostLocalization.TranslateCli("diagnostic", message, LocaleCatalog.HostLocale));
        }
    }

    public static void WriteProgress(string message)
    {
        if (MachineMode)
        {
            Console.Error.WriteLine(HostLocalization.TranslateCli("progress", message, LocaleCatalog.HostLocale));
        }
        else
        {
            Console.WriteLine(HostLocalization.TranslateCli("progress", message, LocaleCatalog.HostLocale));
        }
    }
}

internal static class CliExitCodes
{
    public static int For(string code)
    {
        return code switch
        {
            "ok" => 0,
            "invalid_arguments" or "validation_error" => 2,
            "not_found" or "ambiguous_target" => 3,
            "resource_busy" or "conflict" or "duplicate_name" => 4,
            "service_unavailable" => 5,
            "operation_forbidden" => 6,
            "execution_failed" or "notification_test_failed" => 7,
            "cancelled" or "timeout" => 8,
            _ => 9,
        };
    }
}
