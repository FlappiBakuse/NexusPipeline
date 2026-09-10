using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.App;
using NexusPipeline.Localization;

namespace NexusPipeline.Cli;

internal static partial class CliCommandRouter
{
    private static int SendFileMutation(CliApiClient client, CliArguments args, string method, string path, string label)
    {
        if (!EnsureOptions(args, "file"))
        {
            return CliExitCodes.For("invalid_arguments");
        }
        if (!TryReadJsonObject(args, out JsonObject? body, out int error))
        {
            return error;
        }
        CliApiResponse response = method == "POST"
            ? client.Post(path, body)
            : client.Put(path, body);
        string localizedLabel = CliText.Label(label);
        return ReturnApi(response, CliText.Get("success.updated", "{label}已更新", ("label", localizedLabel)));
    }

    private static int SendIds(CliApiClient client, CliArguments args, string path, string label)
    {
        if (!EnsureOptions(args, "ids"))
        {
            return CliExitCodes.For("invalid_arguments");
        }
        if (!TryRequireOption(
                args,
                "ids",
                CliText.Get("label.ids", "{label} ID 列表", ("label", CliText.Label(label))),
                out string raw,
                out int error))
        {
            return error;
        }
        string[] ids = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ids.Length == 0 || ids.Any(string.IsNullOrWhiteSpace))
        {
            return CliOutput.WriteFailure(
                "invalid_arguments",
                CliText.Get("error.invalid_ids", "--ids 必须包含逗号分隔的完整 ID 列表"));
        }
        var array = new JsonArray(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
        return ReturnApi(
            client.Put(path, Object(("ids", array))),
            CliText.Get("success.reordered", "{label}顺序已更新", ("label", CliText.Label(label))));
    }

    private static int ReturnApi(CliApiResponse response, string? message = null)
    {
        return response.Succeeded
            ? WriteApiSuccess(response.Body, message)
            : CliOutput.WriteFailure(response.Code, response.Message, response.Body);
    }

    private static int WriteApiSuccess(JsonNode? body, string? message)
    {
        if (CliOutput.MachineMode)
        {
            CliOutput.WriteSuccess(body, message);
            return 0;
        }
        if (!string.IsNullOrWhiteSpace(message))
        {
            CliOutput.WriteDiagnostic(CliText.Get("output.done", "[完成] {message}", ("message", message)));
        }
        if (body is not null)
        {
            Console.WriteLine(body.ToJsonString(NexusPipeline.Utilities.JsonOpts.Indented));
        }
        return 0;
    }

    private static bool TryResolveTarget(
        CliApiClient client,
        string listPath,
        string reference,
        string label,
        out string id,
        out int error)
    {
        id = "";
        CliApiResponse response = client.Get(listPath);
        if (!response.Succeeded)
        {
            error = ReturnApi(response);
            return false;
        }
        if (response.Body is not JsonArray array)
        {
            error = CliOutput.WriteFailure(
                "internal_error",
                CliText.Get(
                    "error.invalid_list",
                    "服务返回的{label}列表格式无效",
                    ("label", CliText.Label(label))));
            return false;
        }
        var candidates = array
            .OfType<JsonObject>()
            .Select(item => new CliTarget(
                item["id"]?.ToString() ?? "",
                item["name"]?.ToString() ?? "",
                item))
            .Where(item => item.Id.Length > 0)
            .ToList();
        TargetResolution<CliTarget> resolution = TargetResolver.Resolve(candidates, reference, item => item.Id, item => item.Name);
        if (resolution.IsFound && resolution.Value is not null)
        {
            id = resolution.Value.Id;
            error = 0;
            return true;
        }
        if (resolution.Kind == TargetResolutionKind.Ambiguous)
        {
            var data = new JsonObject
            {
                ["candidates"] = new JsonArray(resolution.Candidates
                    .Select(item => (JsonNode?)new JsonObject { ["id"] = item.Id, ["name"] = item.Name })
                    .ToArray()),
            };
            error = CliOutput.WriteFailure(
                "ambiguous_target",
                CliText.Get(
                    "error.ambiguous_target",
                    "{label}名称匹配到多个对象：{reference}",
                    ("label", CliText.Label(label)),
                    ("reference", reference)),
                data);
            return false;
        }
        error = CliOutput.WriteFailure(
            "not_found",
            CliText.Get(
                "error.not_found",
                "未找到{label}：{reference}",
                ("label", CliText.Label(label)),
                ("reference", reference)));
        return false;
    }

    private static bool TryReadJsonObject(CliArguments args, out JsonObject? objectNode, out int error)
    {
        objectNode = null;
        error = 0;
        if (!TryReadJson(args, out JsonNode? node, out error))
        {
            return false;
        }
        if (node is not JsonObject json)
        {
            error = CliOutput.WriteFailure(
                "validation_error",
                CliText.Get("error.invalid_json_object", "--file 内容必须是 JSON 对象"));
            return false;
        }
        objectNode = json;
        return true;
    }

    private static bool TryReadJson(CliArguments args, out JsonNode? node, out int error)
    {
        node = null;
        error = 0;
        if (!TryRequireOption(args, "file", "JSON 文件路径（或 -）", out string file, out error))
        {
            return false;
        }
        try
        {
            string text = file == "-"
                ? Console.In.ReadToEnd()
                : File.ReadAllText(file, new UTF8Encoding(false));
            node = JsonNode.Parse(text);
            if (node is null)
            {
                error = CliOutput.WriteFailure("validation_error", CliText.Get("error.empty_json", "JSON 内容为空"));
                return false;
            }
            return true;
        }
        catch (JsonException ex)
        {
            error = CliOutput.WriteFailure(
                "validation_error",
                CliText.Get("error.invalid_json", "JSON 内容无效：{detail}", ("detail", ex.Message)));
            return false;
        }
        catch (Exception ex)
        {
            error = CliOutput.WriteFailure(
                "validation_error",
                CliText.Get("error.read_json", "读取 JSON 文件失败：{detail}", ("detail", ex.Message)));
            return false;
        }
    }

    private static bool TryReadFileBytes(CliArguments args, out byte[]? bytes, out string? fileName, out int error)
    {
        bytes = null;
        fileName = null;
        if (!TryRequireOption(args, "file", "文件路径", out string file, out error))
        {
            return false;
        }
        if (file == "-")
        {
            error = CliOutput.WriteFailure(
                "invalid_arguments",
                CliText.Get("error.stdin_binary", "二进制文件参数不支持使用 stdin（--file -）"));
            return false;
        }
        try
        {
            fileName = file;
            bytes = File.ReadAllBytes(file);
            error = 0;
            return true;
        }
        catch (Exception ex)
        {
            error = CliOutput.WriteFailure(
                "validation_error",
                CliText.Get("error.file_read", "读取文件失败：{detail}", ("detail", ex.Message)));
            return false;
        }
    }

    private static bool TryRequirePositional(CliArguments args, int index, string label, out string value, out int error)
    {
        value = "";
        if (args.Positionals.Count <= index || string.IsNullOrWhiteSpace(args.Positionals[index]))
        {
            error = CliOutput.WriteFailure(
                "invalid_arguments",
                CliText.Get("error.missing", "缺少{label}", ("label", CliText.Label(label))));
            return false;
        }
        value = args.Positionals[index];
        error = 0;
        return true;
    }

    private static bool TryRequireOption(CliArguments args, string name, string label, out string value, out int error)
    {
        value = "";
        if (!args.TryGet(name, out string? raw) || raw is null || (name != "secret-value" && string.IsNullOrWhiteSpace(raw)))
        {
            error = CliOutput.WriteFailure(
                "invalid_arguments",
                CliText.Get(
                    "error.missing_option",
                    "缺少 --{name}（{label}）",
                    ("name", name),
                    ("label", CliText.Label(label))));
            return false;
        }
        value = raw;
        error = 0;
        return true;
    }

    private static bool EnsureOptions(CliArguments args, params string[] allowed)
    {
        HashSet<string> accepted = allowed.Select(CliArguments.NormalizeName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string option in args.Options.Keys)
        {
            if (!accepted.Contains(option))
            {
                CliOutput.WriteFailure(
                    "invalid_arguments",
                    CliText.Get("error.unknown_option", "未知选项：--{option}", ("option", option)));
                return false;
            }
        }
        return true;
    }

    private static bool EnsurePositionals(CliArguments args, int expected, string message)
    {
        if (args.Positionals.Count == expected)
        {
            return true;
        }
        CliOutput.WriteFailure("invalid_arguments", message);
        return false;
    }

    private static bool TryRequireOption(CliArguments args, string name, string label, out string value, out int error, bool allowEmpty)
    {
        value = "";
        if (!args.TryGet(name, out string? raw) || raw is null || (!allowEmpty && string.IsNullOrWhiteSpace(raw)))
        {
            error = CliOutput.WriteFailure(
                "invalid_arguments",
                CliText.Get(
                    "error.missing_option",
                    "缺少 --{name}（{label}）",
                    ("name", name),
                    ("label", CliText.Label(label))));
            return false;
        }
        value = raw;
        error = 0;
        return true;
    }

    private static string? Positional(CliArguments args, int index)
    {
        return args.Positionals.Count > index ? args.Positionals[index] : null;
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static string Query(params (string Key, string Value)[] values)
    {
        string[] parts = values
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => Escape(pair.Key) + "=" + Escape(pair.Value))
            .ToArray();
        return parts.Length == 0 ? "" : "?" + string.Join("&", parts);
    }

    private static JsonObject Object(params (string Name, object? Value)[] values)
    {
        var result = new JsonObject();
        foreach ((string name, object? value) in values)
        {
            result[name] = value switch
            {
                null => null,
                JsonNode node => node,
                _ => JsonSerializer.SerializeToNode(value),
            };
        }
        return result;
    }

    private static string MimeFromExtension(string? extension)
    {
        return extension?.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            _ => "",
        };
    }

    private static int WriteUsage()
    {
        string usage = CliText.Get(
            "help.usage",
            "用法：nexus-pipeline.exe <命令> [子命令] [参数]\n\n基础：status、doctor（含 export）\n资源：script、user、queue、run、history、settings、plugin（含 store/user-settings）、update、system-action\n\n机器接口：所有正式命令支持 --json；复杂对象使用 --file <json|->，--file - 从 stdin 读取。\n目标解析：ID 精确优先；名称唯一匹配；同名返回 ambiguous_target。\n进程入口：manage、service、web、restart、register、unregister、apply-update。");
        if (CliOutput.MachineMode)
        {
            CliOutput.WriteSuccess(new JsonObject { ["usage"] = usage });
            return 0;
        }
        Console.WriteLine(CliText.Get("help.title", "NexusPipeline 枢链"));
        Console.WriteLine(usage);
        return 0;
    }

    private sealed record CliTarget(string Id, string Name, JsonObject Data);

}

internal static class CliRouterIntExtensions
{
    public static int AlsoWriteUsage(this int result)
    {
        if (!CliOutput.MachineMode)
        {
            Console.WriteLine(CliText.Get("error.usage", "使用 nexus-pipeline.exe --help 查看命令帮助。"));
        }
        return result;
    }
}
