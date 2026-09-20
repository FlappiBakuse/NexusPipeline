using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;
using NexusPipeline.Modules.Updates;

namespace NexusPipeline.Tests.Updates;

/// <summary>更新域 L1：受限版本解析比较、releases JSON 解析、渠道过滤、主机白名单与当前 zip 合约。</summary>
public sealed class UpdatePackageTests
{
    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "np-update-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteZip(string path, params (string Name, string Content)[] entries)
    {
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach ((string name, string content) in entries)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
    }

    [Fact]
    public void Extract_AcceptsFlatPackageRoot()
    {
        string root = NewTempDir();
        try
        {
            string zip = Path.Combine(root, "pkg.zip");
            WriteZip(zip,
                ("nexus-pipeline.exe", "exe"),
                ("wwwroot/index.js", "app"));
            string staging = Path.Combine(root, "staging");

            string? error = UpdatePackage.Extract(zip, staging);

            Assert.Null(error);
            Assert.True(File.Exists(Path.Combine(staging, "nexus-pipeline.exe")));
            Assert.True(File.Exists(Path.Combine(staging, "wwwroot", "index.js")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Extract_RejectsTraversalAndAbsoluteEntries()
    {
        string root = NewTempDir();
        try
        {
            string staging = Path.Combine(root, "staging");
            Directory.CreateDirectory(staging);

            string traversal = Path.Combine(root, "t.zip");
            WriteZip(traversal, ("../evil.txt", "x"), ("nexus-pipeline.exe", "exe"));
            Assert.NotNull(UpdatePackage.Extract(traversal, staging));

            string absolute = Path.Combine(root, "a.zip");
            WriteZip(absolute, ("C:\\evil.txt", "x"), ("nexus-pipeline.exe", "exe"));
            Assert.NotNull(UpdatePackage.Extract(absolute, staging));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Extract_RejectsForbiddenDataDirectoriesAndDuplicates()
    {
        string root = NewTempDir();
        try
        {
            string staging = Path.Combine(root, "staging");
            Directory.CreateDirectory(staging);

            string forbidden = Path.Combine(root, "f.zip");
            WriteZip(forbidden, ("config/settings.json", "{}"), ("nexus-pipeline.exe", "exe"));
            Assert.NotNull(UpdatePackage.Extract(forbidden, staging));

            string duplicate = Path.Combine(root, "d.zip");
            WriteZip(duplicate, ("wwwroot/a.js", "1"), ("wwwroot/a.js", "2"), ("nexus-pipeline.exe", "exe"));
            Assert.NotNull(UpdatePackage.Extract(duplicate, staging));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Extract_RejectsPackageWithoutExecutable()
    {
        string root = NewTempDir();
        try
        {
            string zip = Path.Combine(root, "p.zip");
            WriteZip(zip, ("readme.txt", "hello"));
            string staging = Path.Combine(root, "staging");

            string? error = UpdatePackage.Extract(zip, staging);

            Assert.NotNull(error);
            Assert.Contains("nexus-pipeline.exe", error);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Extract_RejectsSingleTopDirectoryLayout()
    {
        string root = NewTempDir();
        try
        {
            string zip = Path.Combine(root, "wrapped.zip");
            WriteZip(zip,
                ("NexusPipeline-v0.10.1-win-x64/nexus-pipeline.exe", "exe"),
                ("NexusPipeline-v0.10.1-win-x64/wwwroot/index.js", "app"));

            string? error = UpdatePackage.Extract(zip, Path.Combine(root, "staging"));

            Assert.NotNull(error);
            Assert.Contains("nexus-pipeline.exe", error);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Extract_RejectsArchiveEntryCountAndCompressionRatio()
    {
        string root = NewTempDir();
        try
        {
            string countZip = Path.Combine(root, "count.zip");
            using (var stream = File.Create(countZip))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                for (int index = 0; index <= UpdatePackage.MaxArchiveEntries; index++)
                {
                    archive.CreateEntry($"empty-{index}/");
                }
                using StreamWriter writer = new(archive.CreateEntry("nexus-pipeline.exe").Open(), new UTF8Encoding(false));
                writer.Write("exe");
            }
            Assert.NotNull(UpdatePackage.Extract(countZip, Path.Combine(root, "count-staging")));

            string ratioZip = Path.Combine(root, "ratio.zip");
            using (var stream = File.Create(ratioZip))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                ZipArchiveEntry entry = archive.CreateEntry("nexus-pipeline.exe", CompressionLevel.Optimal);
                using Stream output = entry.Open();
                output.Write(new byte[1024 * 1024]);
            }
            Assert.NotNull(UpdatePackage.Extract(ratioZip, Path.Combine(root, "ratio-staging")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void VerifySha256_MatchesAndDetectsTampering()
    {
        string root = NewTempDir();
        try
        {
            string zip = Path.Combine(root, "pkg.zip");
            File.WriteAllText(zip, "payload");
            string sha = Path.Combine(root, "pkg.sha");
            string expected;
            using (var stream = File.OpenRead(zip))
            using (var hasher = SHA256.Create())
            {
                expected = Convert.ToHexString(hasher.ComputeHash(stream));
            }

            File.WriteAllText(sha, expected + Environment.NewLine);
            Assert.True(UpdatePackage.VerifySha256(zip, sha, out _));

            File.WriteAllText(sha, new string('0', 64) + Environment.NewLine);
            Assert.False(UpdatePackage.VerifySha256(zip, sha, out string? error));
            Assert.Contains("SHA256", error);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

/// <summary>更新域 L2：对接本地 HttpListener stub 源的状态机（检查/下载/校验/应用/取消/互斥）。</summary>
