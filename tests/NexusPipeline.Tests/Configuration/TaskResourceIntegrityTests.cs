using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using NexusPipeline.Modules.Configuration.Scripting;
using Xunit;

namespace NexusPipeline.Tests.Configuration;

public sealed class TaskResourceIntegrityTests
{
    [Theory]
    [InlineData("print('ok')\n", "verified")]
    [InlineData("print('ok')\r\n", "mismatch")]
    [InlineData("\uFEFFprint('ok')\n", "mismatch")]
    [InlineData("print('changed')\n", "mismatch")]
    public void VerifiesExactBytesWithoutExposingDigest(string content, string expected)
    {
        string path = Path.Combine(Path.GetTempPath(), "nxp-integrity-" + Guid.NewGuid().ToString("N"));
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("print('ok')\n"))).ToLowerInvariant();
        try
        {
            File.WriteAllText(path, content, new UTF8Encoding(false));
            var view = new TaskConfigView();
            view.AddResource("code", path, "text", sha256: hash);
            var result = JsonNode.Parse(view.ReadResource("code"))!;
            Assert.Equal(expected, result["integrity"]!.GetValue<string>());
            Assert.DoesNotContain(hash, result.ToJsonString());
            if (expected == "verified") Assert.Equal(content, result["document"]!.GetValue<string>());
            else Assert.Null(result["document"]);
            File.WriteAllText(path, "changed after capture");
            Assert.Throws<InvalidDataException>(() => view.VerifyUnchanged());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void UndeclaredIntegrityKeepsLegacyResourceShape()
    {
        string path = Path.Combine(Path.GetTempPath(), "nxp-integrity-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(path, "unchanged");
            var view = new TaskConfigView();
            view.AddResource("code", path, "text");
            var result = JsonNode.Parse(view.ReadResource("code"))!.AsObject();
            Assert.False(result.ContainsKey("integrity"));
            Assert.Equal("unchanged", result["document"]!.GetValue<string>());
        }
        finally { File.Delete(path); }
    }
}
