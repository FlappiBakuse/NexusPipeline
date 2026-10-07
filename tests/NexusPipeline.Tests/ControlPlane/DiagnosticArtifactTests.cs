using System.Security.Cryptography;
using System.Text;
using NexusPipeline.ControlPlane.Cli;
using NexusPipeline.Modules.Diagnostics;
using Xunit;

namespace NexusPipeline.Tests.ControlPlane;

public sealed class DiagnosticArtifactTests
{
    [Fact]
    public void ManagedArtifactRejectsUnknownIdsOutsidePathsAndChangedBytes()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-diagnostic-artifact-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new DiagnosticArtifactStore(root);
            byte[] bytes = Encoding.UTF8.GetBytes("known redacted ZIP fixture");
            string file = Path.Combine(root, "diagnostics.zip"), hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            File.WriteAllBytes(file, bytes);
            var receipt = store.Register(file, bytes.Length, hash);
            using (var stream = store.Open(receipt.ArtifactId))
            {
                Assert.Equal(bytes.Length, stream.Length);
                Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(stream)));
                Assert.Throws<IOException>(() => File.WriteAllText(file, "replacement"));
            }
            Assert.Throws<FileNotFoundException>(() => store.Open("../diagnostics.zip"));
            Assert.Throws<FileNotFoundException>(() => store.Open(Guid.NewGuid().ToString("N")));
            Assert.Throws<InvalidDataException>(() => store.Register(Path.Combine(root + "-other", "diagnostics.zip"), bytes.Length, hash));
            File.WriteAllText(file, "private changed bytes");
            Assert.Throws<InvalidDataException>(() => store.Open(receipt.ArtifactId));
            Assert.Equal("private changed bytes", File.ReadAllText(file));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CliArtifactOutputSupportsChinesePathsAndNeverOverwritesAnExistingTarget()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-cli-output-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes("redacted diagnostic fixture");
            string target = Path.Combine(root, "中文 诊断包.zip"), hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            await CliArtifactOutput.SaveAsync(new MemoryStream(bytes), target, bytes.Length, hash);
            Assert.Equal(bytes, File.ReadAllBytes(target));
            await Assert.ThrowsAsync<IOException>(() => CliArtifactOutput.SaveAsync(new MemoryStream([1, 2, 3]), target, 3, new('a', 64)));
            Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(target))));
            string invalid = Path.Combine(root, "bad.zip");
            await Assert.ThrowsAsync<InvalidDataException>(() => CliArtifactOutput.SaveAsync(new MemoryStream(bytes), invalid, bytes.Length, new('a', 64)));
            Assert.False(File.Exists(invalid));
            await Assert.ThrowsAsync<DirectoryNotFoundException>(() => CliArtifactOutput.SaveAsync(new MemoryStream(bytes), Path.Combine(root, "missing", "out.zip"), bytes.Length, hash));
        }
        finally { Directory.Delete(root, true); }
    }
}
