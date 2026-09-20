using System.Text.Json.Nodes;
using Xunit;
using NexusPipeline.ControlPlane.Http;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Tests.Platform;

/// <summary>KN-01：损坏配置文件解析失败时改名保留，避免后续保存静默覆盖原数据。</summary>
public class JsonStoreTests
{
    [Fact]
    public void WriteAtomic_FailureBeforeReplacePreservesOriginalAndOwnedTemporaryFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "nexus-json-phase-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "state.json");
            File.WriteAllText(path, "{\"original\":true}");

            IOException error = Assert.Throws<IOException>(() => JsonUtil.WriteAtomic(
                path,
                "{\"replacement\":true}",
                phase =>
                {
                    if (phase == JsonWritePhase.BeforeReplace)
                    {
                        throw new IOException("模拟替换前写入失败");
                    }
                }));

            Assert.Contains("模拟替换前", error.Message);
            Assert.Equal("{\"original\":true}", File.ReadAllText(path));
            Assert.Empty(Directory.GetFiles(dir, ".state.json.*.tmp"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void WriteAtomic_SimulatedDiskFullAfterFlushPreservesOriginal()
    {
        string dir = Path.Combine(Path.GetTempPath(), "nexus-json-diskfull-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "state.json");
            File.WriteAllText(path, "{\"original\":true}");
            const int DiskFullHResult = unchecked((int)0x80070070);

            IOException error = Assert.Throws<IOException>(() => JsonUtil.WriteAtomic(
                path,
                "{\"replacement\":true}",
                phase =>
                {
                    if (phase == JsonWritePhase.TempFlushed)
                    {
                        throw new IOException("模拟指定 disk-full 故障", DiskFullHResult);
                    }
                }));

            Assert.Equal(DiskFullHResult, error.HResult);
            Assert.Equal("{\"original\":true}", File.ReadAllText(path));
            Assert.Empty(Directory.GetFiles(dir, ".state.json.*.tmp"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void WriteAtomic_FailureAfterReplaceLeavesCompleteNewValue()
    {
        string dir = Path.Combine(Path.GetTempPath(), "nexus-json-after-replace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "state.json");
            File.WriteAllText(path, "{\"original\":true}");

            Assert.Throws<IOException>(() => JsonUtil.WriteAtomic(
                path,
                "{\"replacement\":true}",
                phase =>
                {
                    if (phase == JsonWritePhase.AfterReplace)
                    {
                        throw new IOException("模拟提交后返回失败");
                    }
                }));

            Assert.Equal("{\"replacement\":true}", File.ReadAllText(path));
            Assert.NotNull(JsonNode.Parse(File.ReadAllText(path)));
            Assert.Empty(Directory.GetFiles(dir, ".state.json.*.tmp"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void WriteAtomic_LockedTargetPreservesOriginalAndRemovesOwnedTemporaryFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "nexus-json-locked-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "state.json");
            File.WriteAllText(path, "{\"original\":true}");
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Exception? error = Record.Exception(() => JsonUtil.WriteAtomic(path, "{\"replacement\":true}"));
                Assert.True(error is IOException or UnauthorizedAccessException);
            }
            Assert.Equal("{\"original\":true}", File.ReadAllText(path));
            Assert.Empty(Directory.GetFiles(dir, ".state.json.*.tmp"));
            JsonUtil.WriteAtomic(path, "{\"retry\":true}");
            Assert.Equal("{\"retry\":true}", File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ReadObject_PreservationFailureRetainsCorruptBytesAndBlocksDefaultOverwrite()
    {
        string dir = Path.Combine(Path.GetTempPath(), "nexus-json-preserve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "state.json");
            File.WriteAllText(path, "{broken");
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.ThrowsAny<IOException>(() => JsonStore.ReadObjectOrEmpty(path, "locked test"));
            Assert.Equal("{broken", File.ReadAllText(path));
            Assert.Empty(Directory.GetFiles(dir, "*.corrupt-*"));
            Assert.Empty(JsonStore.ReadObjectOrEmpty(path, "unlocked test"));
            Assert.Equal("{broken", File.ReadAllText(Assert.Single(Directory.GetFiles(dir, "*.corrupt-*"))));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

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

    [Fact]
    public void ReadObjectOrEmpty_CorruptFile_PreservesOriginal()
    {
        string dir = Path.Combine(Path.GetTempPath(), "nexus-jsontest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "secrets.json");
            string corrupt = "[not-an-object]";
            File.WriteAllText(path, corrupt);

            JsonObject result = JsonStore.ReadObjectOrEmpty(path, "测试插件密钥");

            Assert.Empty(result);
            Assert.False(File.Exists(path));
            string? preserved = Directory.GetFiles(dir, "secrets.json.corrupt-*").SingleOrDefault();
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
    public async Task WriteAtomic_ConcurrentWritersLeaveOneCompletePayloadAndNoOwnedTemps()
    {
        string dir = Path.Combine(Path.GetTempPath(), "nexus-jsontest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "state.json");
            string[] payloads = Enumerable.Range(0, 32)
                .Select(index => $"{{\"writer\":{index},\"value\":\"{Guid.NewGuid():N}\"}}")
                .ToArray();

            await Task.WhenAll(payloads.Select(payload =>
                Task.Run(() => JsonUtil.WriteAtomic(path, payload))));

            string actual = File.ReadAllText(path);
            Assert.Contains(actual, payloads);
            Assert.NotNull(JsonNode.Parse(actual));
            Assert.Empty(Directory.GetFiles(dir, ".state.json.*.tmp"));
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
