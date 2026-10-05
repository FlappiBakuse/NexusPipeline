using System.Security.Cryptography;
using System.Text;
using NexusPipeline.Modules.Configuration.Scripting;

namespace NexusPipeline.Modules.Configuration.Snapshots;

internal static class ConfigSnapshotFingerprint
{
    internal static string Fingerprint(string path)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int count = 0;
        long size = 0;
        void Read(string current, string relative, int depth)
        {
            if (depth > 8 || ++count > 512) throw new IOException("config_resource_limit: 配置文件数量或层级过大");
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                hash.AppendData(Encoding.UTF8.GetBytes("missing:" + relative + "\n"));
                return;
            }
            TaskConfigView.ValidatePath(current);
            if (Directory.Exists(current))
            {
                hash.AppendData(Encoding.UTF8.GetBytes("directory:" + relative + "\n"));
                foreach (string child in Directory.EnumerateFileSystemEntries(current).Order(StringComparer.Ordinal))
                    Read(child, relative + "/" + Path.GetFileName(child), depth + 1);
                return;
            }
            size += new FileInfo(current).Length;
            if (size > 64 * 1024 * 1024) throw new IOException("config_resource_limit: 配置超过指纹大小上限");
            byte[] bytes = File.ReadAllBytes(current);
            hash.AppendData(Encoding.UTF8.GetBytes("file:" + relative + ":" + bytes.Length + "\n"));
            hash.AppendData(bytes);
        }
        Read(path, "", 0);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

}
