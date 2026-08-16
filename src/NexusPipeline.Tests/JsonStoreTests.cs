using NexusPipeline.Models;
using NexusPipeline.Persistence;
using Xunit;

namespace NexusPipeline.Tests;

/// <summary>KN-01：损坏配置文件解析失败时改名保留，避免后续保存静默覆盖原数据。</summary>
public class JsonStoreTests
{
    [Fact]
    public void LoadList_CorruptFile_ReturnsEmptyAndPreservesOriginal()
    {
        string dir = Path.Combine(Path.GetTempPath(), "nexus-jsontest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "scripts.json");
            string corrupt = "{ 这不是合法 JSON \u0001\u0002";
            File.WriteAllText(path, corrupt);

            List<ScriptInstance> result = JsonStore.LoadList<ScriptInstance>(path);

            Assert.Empty(result);
            Assert.False(File.Exists(path), "损坏文件应被改名，原路径不再存在（后续保存不得覆盖损坏数据）");
            string? preserved = Directory.GetFiles(dir, "scripts.json.corrupt-*").SingleOrDefault();
            Assert.NotNull(preserved);
            Assert.Equal(corrupt, File.ReadAllText(preserved!));
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void LoadList_ValidFile_LoadsAndKeepsFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "nexus-jsontest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "scripts.json");
            string valid = "[{\"Id\":\"abc\",\"Name\":\"脚本\"}]";
            File.WriteAllText(path, valid);

            List<ScriptInstance> result = JsonStore.LoadList<ScriptInstance>(path);

            ScriptInstance? item = Assert.Single(result);
            Assert.Equal("abc", item.Id);
            Assert.Equal("脚本", item.Name);
            Assert.True(File.Exists(path), "合法文件保持原路径不变");
            Assert.Empty(Directory.GetFiles(dir, "*.corrupt-*"));
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
            }
        }
    }
}
