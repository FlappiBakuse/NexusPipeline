using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Host.Composition;
using NexusPipeline.Modules.Configuration.Paths;
using NexusPipeline.Modules.Configuration.Recovery;
using NexusPipeline.Modules.Configuration.Snapshots;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Users;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Testing;
using NexusPipeline.Shared.Logging;

namespace NexusPipeline.Modules.Configuration.Exchange;

internal static partial class ConfigSwapSession
{
    /// <summary>还原描述：判断脚本首次触发写入 script/config-restore.json，宿主仅执行、不解析插件语义。
    /// 契约见 docs/reference/plugin-api/README.md。</summary>
    internal sealed class ConfigRestoreDescriptor
    {
        public List<FileRestore> Files { get; set; } = new();
    }

    internal sealed class FileRestore
    {
        public string File { get; set; } = "";

        public List<ToggleRestore> Toggles { get; set; } = new();
    }

    internal sealed class ToggleRestore
    {
        public string Type { get; set; } = "";

        public string Path { get; set; } = "";

        public string KeyField { get; set; } = "";

        public string EnabledField { get; set; } = "";

        public Dictionary<string, bool> Initial { get; set; } = new();

        /// <summary>boolArray 型：与目标布尔数组按下标对应的初始值（BAAH 平行数组 TASK_ONOFF 等场景）。</summary>
        public List<bool> InitialList { get; set; } = new();
    }

    internal static ConfigRestoreDescriptor? ReadRestoreDescriptor(string scriptDir)
    {
        string file = Path.Combine(scriptDir, "config-restore.json");
        if (!File.Exists(file))
        {
            return null;
        }
        try
        {
            JsonNode? node = JsonNode.Parse(File.ReadAllText(file));
            var descriptor = new ConfigRestoreDescriptor();
            if (node?["files"] is JsonArray files)
            {
                foreach (JsonNode? item in files)
                {
                    if (item is null)
                    {
                        continue;
                    }
                    var fr = new FileRestore { File = item["file"]?.ToString() ?? "" };
                    if (item["toggles"] is JsonArray toggles)
                    {
                        foreach (JsonNode? t in toggles)
                        {
                            if (t is null)
                            {
                                continue;
                            }
                            var tr = new ToggleRestore
                            {
                                Type = t["type"]?.ToString() ?? "",
                                Path = t["path"]?.ToString() ?? "",
                                KeyField = t["keyField"]?.ToString() ?? "",
                                EnabledField = t["enabledField"]?.ToString() ?? "",
                            };
                            if (t["initial"] is JsonObject initial)
                            {
                                foreach (KeyValuePair<string, JsonNode?> kv in initial)
                                {
                                    if (kv.Value is not null && bool.TryParse(kv.Value.ToString(), out bool b))
                                    {
                                        tr.Initial[kv.Key] = b;
                                    }
                                }
                            }
                            if (t["initial"] is JsonArray initialList)
                            {
                                foreach (JsonNode? entry in initialList)
                                {
                                    if (entry is not null && bool.TryParse(entry.ToString(), out bool b))
                                    {
                                        tr.InitialList.Add(b);
                                    }
                                }
                            }
                            bool hasPayload = tr.Type == "boolArray" ? tr.InitialList.Count > 0 : tr.Initial.Count > 0;
                            if (!string.IsNullOrWhiteSpace(tr.Type) && !string.IsNullOrWhiteSpace(tr.Path) && hasPayload)
                            {
                                fr.Toggles.Add(tr);
                            }
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(fr.File) && fr.Toggles.Count > 0)
                    {
                        descriptor.Files.Add(fr);
                    }
                }
            }
            return descriptor.Files.Count > 0 ? descriptor : null;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[配置同步] 还原描述解析失败（{file}）：{ex.Message}");
            return null;
        }
    }

    internal static (string Content, Encoding Encoding) ReadTextPreservingEncoding(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        byte[] prefix = new byte[4];
        int read = stream.Read(prefix, 0, prefix.Length);
        bool hasBom = (read >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF)
            || (read >= 2 && prefix[0] == 0xFF && prefix[1] == 0xFE)
            || (read >= 2 && prefix[0] == 0xFE && prefix[1] == 0xFF)
            || (read >= 4 && prefix[0] == 0xFF && prefix[1] == 0xFE && prefix[2] == 0x00 && prefix[3] == 0x00)
            || (read >= 4 && prefix[0] == 0x00 && prefix[1] == 0x00 && prefix[2] == 0xFE && prefix[3] == 0xFF);
        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string content = reader.ReadToEnd();
        Encoding detected = reader.CurrentEncoding;
        Encoding output = detected.CodePage switch
        {
            65001 => new UTF8Encoding(hasBom),
            1200 => new UnicodeEncoding(false, hasBom),
            1201 => new UnicodeEncoding(true, hasBom),
            12000 => new UTF32Encoding(false, hasBom),
            12001 => new UTF32Encoding(true, hasBom),
            _ => detected,
        };
        return (content, output);
    }

    internal static FileRestore? FindRestoreFile(ConfigRestoreDescriptor? descriptor, string rel)
    {
        return descriptor?.Files.FirstOrDefault(file =>
            string.Equals(NormalizeRelative(file.File), NormalizeRelative(rel), StringComparison.OrdinalIgnoreCase));
    }

    internal static string NormalizeRelative(string value)
    {
        return ConfigStoreDiff.NormalizeRelative(value);
    }

    /// <summary>应用单个还原描述 toggle：array 型按 keyField 匹配 initial 设 enabledField（未覆盖元素不动）；map 型逐键设布尔（未覆盖键不动）；
    /// boolArray 型按下标还原布尔数组（短于 initial 视为失败）。</summary>
    internal static bool ApplyToggle(ref string content, ToggleRestore toggle)
    {
        try
        {
            JsonNode? root = JsonNode.Parse(content);
            if (root is null)
            {
                return false;
            }
            if (toggle.Type == "array")
            {
                JsonNode? node = LocateNode(root, toggle.Path);
                if (node is not JsonArray array)
                {
                    return false;
                }
                foreach (JsonNode? element in array)
                {
                    if (element is null)
                    {
                        continue;
                    }
                    string? key = element[toggle.KeyField]?.ToString();
                    if (string.IsNullOrWhiteSpace(key) || !toggle.Initial.TryGetValue(key, out bool value))
                    {
                        continue;
                    }
                    element[toggle.EnabledField] = value;
                }
            }
            else if (toggle.Type == "map")
            {
                JsonNode? node = LocateNode(root, toggle.Path);
                if (node is not JsonObject obj)
                {
                    return false;
                }
                foreach (KeyValuePair<string, bool> kv in toggle.Initial)
                {
                    obj[kv.Key] = kv.Value;
                }
            }
            else if (toggle.Type == "boolArray")
            {
                JsonNode? node = LocateNode(root, toggle.Path);
                if (node is not JsonArray array)
                {
                    return false;
                }
                // 按下标还原布尔数组；目标数组短于 initial 说明配置结构与首次触发时不同，视为应用失败（该文件不入快照）。
                if (array.Count < toggle.InitialList.Count)
                {
                    return false;
                }
                for (int index = 0; index < toggle.InitialList.Count; index++)
                {
                    array[index] = toggle.InitialList[index];
                }
            }
            else
            {
                return false;
            }
            content = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>还原描述路径 DSL 定位：支持标识符[下标]或标识符[key=value]选择数组元素（如 instances[id=main].tasks）。</summary>
    internal static JsonNode? LocateNode(JsonNode root, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        JsonNode? node = root;
        foreach (string segment in path.Split('.'))
        {
            string name = segment;
            int index = -1;
            string? selectorKey = null;
            string? selectorValue = null;
            int bracket = segment.IndexOf('[');
            if (bracket >= 0 && segment.Length > bracket + 1 && segment[^1] == ']')
            {
                name = segment.Substring(0, bracket);
                string selector = segment.Substring(bracket + 1, segment.Length - bracket - 2);
                if (!int.TryParse(selector, out index))
                {
                    index = -1;
                    int equal = selector.IndexOf('=');
                    if (equal <= 0 || equal >= selector.Length - 1)
                    {
                        return null;
                    }
                    selectorKey = selector[..equal];
                    selectorValue = selector[(equal + 1)..];
                }
            }
            if (node is JsonObject obj)
            {
                if (string.IsNullOrEmpty(name))
                {
                    return null;
                }
                node = obj[name];
                if (selectorKey is not null)
                {
                    if (node is not JsonArray selectedArray)
                    {
                        return null;
                    }
                    JsonNode? selectedNode = null;
                    foreach (JsonNode? item in selectedArray)
                    {
                        if (item is not JsonObject selected)
                        {
                            continue;
                        }
                        string? actual = selected[selectorKey!]?.ToString()?.Trim('"');
                        if (string.Equals(actual, selectorValue, StringComparison.Ordinal))
                        {
                            selectedNode = selected;
                            break;
                        }
                    }
                    node = selectedNode;
                }
            }
            else if (node is JsonArray arr)
            {
                if (index < 0 || index >= arr.Count)
                {
                    return null;
                }
                node = arr[index];
                index = -1;
            }
            else
            {
                return null;
            }
            if (node is null)
            {
                return null;
            }
            if (index >= 0)
            {
                if (node is not JsonArray arr2 || index >= arr2.Count)
                {
                    return null;
                }
                node = arr2[index];
            }
        }
        return node;
    }

}
