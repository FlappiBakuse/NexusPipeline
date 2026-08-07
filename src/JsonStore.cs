using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NexusPipeline;

public static class JsonOpts
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static readonly JsonSerializerOptions Indented = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static readonly JsonSerializerOptions Web = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}

public static class JsonUtil
{
    public static void WriteAtomic(string path, string content)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(true));
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        File.Move(temp, path);
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

    public static List<string> StringList(this JsonNode? node)
    {
        var result = new List<string>();
        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                result.Add(item.Str());
            }
        }
        return result;
    }
}

public static class JsonStore
{
    public static List<T> LoadList<T>(string path) where T : new()
    {
        var list = new List<T>();
        if (File.Exists(path))
        {
            try
            {
                List<T>? parsed = JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path), JsonOpts.Default);
                if (parsed is not null)
                {
                    list = parsed;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[警告] 解析 {Path.GetFileName(path)} 失败：{ex.Message}");
            }
        }
        return list;
    }

    public static void SaveList<T>(string path, List<T> list)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppPaths.ConfigDir);
        JsonUtil.WriteAtomic(path, JsonSerializer.Serialize(list, JsonOpts.Indented));
    }
}
