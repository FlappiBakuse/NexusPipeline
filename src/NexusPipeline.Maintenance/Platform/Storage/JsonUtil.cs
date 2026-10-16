using System.Text;
using System.Text.Json.Nodes;
namespace NexusPipeline.Platform.Storage;

internal enum JsonWritePhase
{
    TempCreated,
    TempFlushed,
    BeforeReplace,
    AfterReplace,
}

internal static class JsonUtil
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly object[] WriteLocks = Enumerable.Range(0, 64).Select(_ => new object()).ToArray();

    /// <summary>
    /// 将 JSON 写入同目录唯一临时文件并替换目标。phaseObserver 只供内部事务测试在真实文件边界注入受控故障；
    /// 正常生产调用不传入它，写入路径与磁盘协议保持不变。
    /// </summary>
    public static void WriteAtomic(
        string path,
        string content,
        Action<JsonWritePhase>? phaseObserver = null)
    {
        string target = Path.GetFullPath(path);
        // 固定分片保留同一路径的互斥，同时限制长运行实例的锁对象数量。
        object writeLock = WriteLocks[(uint)PathComparer.GetHashCode(target) % (uint)WriteLocks.Length];
        lock (writeLock)
        {
            string directory = Path.GetDirectoryName(target)
                ?? throw new IOException($"无法确定 JSON 目标目录：{target}");
            string temp = Path.Combine(
                directory,
                $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(
                           temp,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           bufferSize: 4096,
                           FileOptions.SequentialScan))
                {
                    phaseObserver?.Invoke(JsonWritePhase.TempCreated);
                    using (var writer = new StreamWriter(
                               stream,
                               new UTF8Encoding(true),
                               bufferSize: 4096,
                               leaveOpen: true))
                    {
                        writer.Write(content);
                        writer.Flush();
                    }
                    stream.Flush(flushToDisk: true);
                }
                phaseObserver?.Invoke(JsonWritePhase.TempFlushed);
                phaseObserver?.Invoke(JsonWritePhase.BeforeReplace);
                File.Move(temp, target, overwrite: true);
                phaseObserver?.Invoke(JsonWritePhase.AfterReplace);
            }
            finally
            {
                try
                {
                    if (File.Exists(temp))
                    {
                        File.Delete(temp);
                    }
                }
                catch
                {
                    // 临时文件清理失败时保留现场，不覆盖原始写入异常。
                }
            }
        }
    }

    public static JsonNode? Get(this JsonNode? node, string key)
    {
        return node is JsonObject obj ? obj[key] : null;
    }

    public static string Str(this JsonNode? node)
    {
        if (node is null)
        {
            return "";
        }
        if (node is JsonValue value && value.TryGetValue<string>(out string? s))
        {
            return s;
        }
        return node.ToJsonString().Trim('"');
    }

    public static bool Bool(this JsonNode? node, bool defaultValue = false)
    {
        if (node is JsonValue value && value.TryGetValue<bool>(out bool b))
        {
            return b;
        }
        return bool.TryParse(node?.Str(), out bool parsed) ? parsed : defaultValue;
    }

    public static int Int(this JsonNode? node, int defaultValue = 0)
    {
        if (node is JsonValue value && value.TryGetValue<int>(out int i))
        {
            return i;
        }
        return int.TryParse(node?.Str(), out int parsed) ? parsed : defaultValue;
    }
}
