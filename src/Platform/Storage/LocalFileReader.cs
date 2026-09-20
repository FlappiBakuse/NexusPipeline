using System.Text;

namespace NexusPipeline.Platform.Storage;

/// <summary>控制面需要的有限本地文件读取/清理适配；文件系统 API 只在 Platform 层出现。</summary>
internal static class LocalFileReader
{
    internal static bool Exists(string path) => File.Exists(path);

    internal static byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

    internal static string ReadAllText(string path, Encoding encoding) => File.ReadAllText(path, encoding);

    internal static void Delete(string path) => File.Delete(path);
}
