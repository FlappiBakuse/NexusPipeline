using System.Text.Json;
using System.Text.Json.Serialization;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services;

/// <summary>判定脚本可见的进程投影；不包含路径、命令行或句柄。</summary>
internal sealed record JudgeProcessInfo(
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("ppid")] int Ppid,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("startTimeUtc")] DateTime? StartTimeUtc);

internal sealed record JudgeProcessResponse(
    [property: JsonPropertyName("processes")] IReadOnlyList<JudgeProcessInfo> Processes,
    [property: JsonPropertyName("truncated")] bool Truncated);

/// <summary>判定脚本可见的窗口投影；hwnd 以字符串序列化，避免 JavaScript 整数精度损失。</summary>
internal sealed record JudgeWindowInfo(
    [property: JsonPropertyName("hwnd")] string Hwnd,
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("foreground")] bool Foreground);

internal sealed record JudgeWindowResponse(
    [property: JsonPropertyName("windows")] IReadOnlyList<JudgeWindowInfo> Windows,
    [property: JsonPropertyName("truncated")] bool Truncated);

/// <summary>判定探针的只读快照实现。</summary>
internal static class JudgeProbeService
{
    internal const int MaxProcesses = 2048;
    internal const int MaxWindows = 512;
    internal const int MaxFilterLength = 256;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static string ListProcessesJson(string? optionsJson = null)
    {
        (string nameContains, int? pid) = ReadProcessOptions(optionsJson);
        IReadOnlyDictionary<int, ProcessTree.ProcessNode> nodes = ProcessTree.SnapshotProcesses();
        return Serialize(ProjectProcesses(nodes, nameContains, pid, ProcessTree.CaptureProcessIdentity));
    }

    internal static string ListWindowsJson(string? optionsJson = null)
    {
        (string titleContains, int? pid) = ReadWindowOptions(optionsJson);
        IReadOnlyDictionary<int, ProcessTree.ProcessNode> nodes = ProcessTree.SnapshotProcesses();
        var windows = ProcessWindows.EnumerateVisibleTopLevelWindows()
            .Where(window => !pid.HasValue || window.Pid == pid.Value)
            .Where(window => titleContains.Length == 0
                || window.Title.Contains(titleContains, StringComparison.OrdinalIgnoreCase))
            .OrderBy(window => window.Pid)
            .ThenBy(window => window.Hwnd.ToInt64())
            .Take(MaxWindows + 1)
            .Select(window => new JudgeWindowInfo(
                window.Hwnd.ToInt64().ToString(),
                window.Pid,
                nodes.TryGetValue(window.Pid, out ProcessTree.ProcessNode? node) ? node.ExeName : "",
                window.Title.Length > MaxFilterLength ? window.Title[..MaxFilterLength] : window.Title,
                window.Foreground))
            .ToList();
        bool truncated = windows.Count > MaxWindows;
        if (truncated)
        {
            windows.RemoveRange(MaxWindows, windows.Count - MaxWindows);
        }
        return Serialize(new JudgeWindowResponse(windows, truncated));
    }

    internal static JudgeProcessResponse ProjectProcesses(
        IReadOnlyDictionary<int, ProcessTree.ProcessNode> nodes,
        string? nameContains = null,
        int? pid = null,
        Func<int, ProcessIdentity?>? identityProvider = null,
        int maxResults = MaxProcesses)
    {
        string filter = BoundFilter(nameContains);
        identityProvider ??= ProcessTree.CaptureProcessIdentity;
        var processes = nodes.Values
            .Where(node => !pid.HasValue || node.Pid == pid.Value)
            .Where(node => filter.Length == 0 || node.ExeName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(node => node.Pid)
            .Take(maxResults + 1)
            .Select(node =>
            {
                DateTime? startTime = null;
                try
                {
                    startTime = identityProvider(node.Pid)?.StartTime;
                }
                catch
                {
                    // 进程在快照与投影之间退出时，保留进程条目而省略易失身份字段。
                }
                return new JudgeProcessInfo(node.Pid, node.Ppid, node.ExeName, startTime);
            })
            .ToList();
        bool truncated = processes.Count > maxResults;
        if (truncated)
        {
            processes.RemoveRange(maxResults, processes.Count - maxResults);
        }
        return new JudgeProcessResponse(processes, truncated);
    }

    internal static string Serialize(object value) => JsonSerializer.Serialize(value, JsonOptions);

    private static (string NameContains, int? Pid) ReadProcessOptions(string? json)
    {
        try
        {
            using JsonDocument document = ParseOptions(json);
            JsonElement root = document.RootElement;
            return (ReadFilter(root, "nameContains"), ReadPid(root));
        }
        catch (JsonException)
        {
            return ("", null);
        }
    }

    private static (string TitleContains, int? Pid) ReadWindowOptions(string? json)
    {
        try
        {
            using JsonDocument document = ParseOptions(json);
            JsonElement root = document.RootElement;
            return (ReadFilter(root, "titleContains"), ReadPid(root));
        }
        catch (JsonException)
        {
            return ("", null);
        }
    }

    private static JsonDocument ParseOptions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return JsonDocument.Parse("{}");
        }
        if (json.Length > 4096)
        {
            throw new JsonException("probe options too large");
        }
        JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new JsonException("probe options must be an object");
        }
        return document;
    }

    private static string ReadFilter(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? BoundFilter(value.GetString())
            : "";
    }

    private static int? ReadPid(JsonElement root)
    {
        if (!root.TryGetProperty("pid", out JsonElement value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out int pid)
            || pid <= 0)
        {
            return null;
        }
        return pid;
    }

    private static string BoundFilter(string? value)
    {
        string filter = value?.Trim() ?? "";
        return filter.Length > MaxFilterLength ? filter[..MaxFilterLength] : filter;
    }
}
