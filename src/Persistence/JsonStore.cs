using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Utilities;

namespace NexusPipeline.Persistence;

internal static class JsonUtil
{
    public static void WriteAtomic(string path, string content)
    {
        string temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, content, new UTF8Encoding(true));
            File.Move(temp, path, overwrite: true);
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

internal static class JsonStore
{
    /// <summary>
    /// 损坏配置文件改名保留：解析失败时先把原文件改名为 {path}.corrupt-{时间戳}，
    /// 避免后续任意一次保存静默覆盖损坏文件导致原数据不可恢复；用户可手动用保留文件恢复。
    /// 返回保留路径；改名失败返回空字符串（不中断加载流程）。
    /// </summary>
    public static string PreserveCorruptFile(string path)
    {
        try
        {
            string backup = $"{path}.corrupt-{DateTime.Now:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
            File.Move(path, backup);
            return backup;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 保留损坏配置失败（{path}）：{ex.Message}");
            return "";
        }
    }

    /// <summary>读取插件数据文件；JSON 解析失败时保留原文件，后续写入不会覆盖损坏现场。</summary>
    public static bool TryRead<T>(string path, out T? value, string label)
    {
        value = default;
        if (!File.Exists(path))
        {
            return true;
        }
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            Logger.Warn($"{label}读取失败（{path}）：{ex.Message}");
            return false;
        }
        try
        {
            value = JsonSerializer.Deserialize<T>(text, JsonOpts.Default);
            return true;
        }
        catch (JsonException ex)
        {
            string backup = PreserveCorruptFile(path);
            Logger.Warn($"{label}解析失败（{path}）：{ex.Message}，原文件已保留为 {backup}");
            return false;
        }
        catch (NotSupportedException ex)
        {
            string backup = PreserveCorruptFile(path);
            Logger.Warn($"{label}格式不受支持（{path}）：{ex.Message}，原文件已保留为 {backup}");
            return false;
        }
        catch (InvalidOperationException ex)
        {
            string backup = PreserveCorruptFile(path);
            Logger.Warn($"{label}结构无效（{path}）：{ex.Message}，原文件已保留为 {backup}");
            return false;
        }
    }

    public static JsonObject ReadObjectOrEmpty(string path, string label)
    {
        if (!File.Exists(path))
        {
            return new JsonObject();
        }
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            Logger.Warn($"{label}读取失败（{path}）：{ex.Message}");
            return new JsonObject();
        }
        try
        {
            if (JsonNode.Parse(text) is JsonObject root)
            {
                return root;
            }
            throw new InvalidDataException("JSON 根节点必须是对象");
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            string backup = PreserveCorruptFile(path);
            Logger.Warn($"{label}解析失败（{path}）：{ex.Message}，原文件已保留为 {backup}");
            return new JsonObject();
        }
    }

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
                string backup = PreserveCorruptFile(path);
                Logger.Warn($"[警告] 解析 {Path.GetFileName(path)} 失败：{ex.Message}，原文件已保留为 {Path.GetFileName(backup)}（可手动恢复，不再被后续保存覆盖）");
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
