using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Runtime.Versioning;
using System.Text;
using NexusPipeline.Platform.Storage.Archives;
using NexusPipeline.Platform.Storage.Maintenance;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class BundleProfileTests
{
    [Fact]
    public void ReadsTestBundle()
    {
        using var input = new MemoryStream(Bundle());
        UpdateBuildProfile.RequireBundle(input, "NexusPipeline.Host.dll", "test");
        input.Position = 0;
        Assert.Equal(File.ReadAllBytes(Path.ChangeExtension(Host(), ".dll")),
            DotnetBundle.ReadAssembly(input, "NexusPipeline.Host.dll"));
    }

    [Theory]
    [InlineData("production")]
    [InlineData("unknown")]
    public void RejectsWrongProfile(string profile)
    {
        using var input = new MemoryStream(Bundle());
        var error = Assert.Throws<IOException>(() => UpdateBuildProfile.RequireBundle(input, "NexusPipeline.Host.dll", profile));
        Assert.Equal("update_payload_profile_invalid", error.Message);
    }

    [Theory]
    [InlineData("marker")]
    [InlineData("version")]
    [InlineData("outside")]
    [InlineData("overlap")]
    [InlineData("compressed")]
    [InlineData("duplicate")]
    [InlineData("path")]
    [InlineData("config")]
    [InlineData("truncated")]
    public void RejectsBundleData(string fault)
    {
        using var input = new MemoryStream(Bundle(fault));
        Assert.Throws<IOException>(() => DotnetBundle.ReadAssembly(input, "NexusPipeline.Host.dll"));
    }

    [Fact]
    public void RejectsMissingAssembly()
    {
        using var input = new MemoryStream(Bundle());
        Assert.Throws<IOException>(() => DotnetBundle.ReadAssembly(input, "Unbound.dll"));
    }

    [Theory]
    [InlineData("missing-sdk")]
    [InlineData("sdk-data")]
    [InlineData("sdk-identity")]
    public void RejectsSdkIdentity(string fault)
    {
        using var input = new MemoryStream(Bundle(fault));
        Assert.Throws<IOException>(() => UpdateBuildProfile.RequireBundle(input, "NexusPipeline.Host.dll", "test"));
    }

    private static string Host() => Environment.GetEnvironmentVariable("NEXUS_TEST_HOST_EXE")
        ?? throw new InvalidOperationException("test_host_required");

    private static byte[] Bundle(string fault = "")
    {
        var marker = Convert.FromHexString("8b1202b96a612038727b930214d7a03213f5b9e6efae3318ee3b2dce24b36aae");
        var native = File.ReadAllBytes(Host());
        using var source = new PEReader(new MemoryStream(native));
        var extent = source.PEHeaders.SectionHeaders.Max(section => section.PointerToRawData + section.SizeOfRawData);
        var found = native.AsSpan(0, extent).IndexOf(marker);
        Assert.True(found >= 8, "Actual SDK apphost has no bundle marker");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(native);
        var entries = new List<(string Name, long Offset, long Size, byte Kind)>();
        void Add(string name, byte kind, byte[] bytes)
        {
            entries.Add((name, stream.Position, bytes.Length, kind)); writer.Write(bytes);
        }
        Add("NexusPipeline.Host.dll", 1, File.ReadAllBytes(Path.ChangeExtension(Host(), ".dll")));
        if (fault != "missing-sdk")
            Add("NexusPipeline.Extensibility.dll", 1, fault == "sdk-data" ? [1, 2, 3] : File.ReadAllBytes(fault == "sdk-identity"
                ? Path.ChangeExtension(Host(), ".dll") : Path.Combine(Path.GetDirectoryName(Host())!, "NexusPipeline.Extensibility.dll")));
        Add("NexusPipeline.Host.deps.json", 3, "{}"u8.ToArray());
        Add("NexusPipeline.Host.runtimeconfig.json", 4, "{}"u8.ToArray());
        var offset = stream.Position;
        var deps = entries.Single(item => item.Name.EndsWith(".deps.json", StringComparison.Ordinal));
        var config = entries.Single(item => item.Name.EndsWith(".runtimeconfig.json", StringComparison.Ordinal));
        writer.Write(fault == "version" ? 7u : 6u); writer.Write(0u); writer.Write(entries.Count);
        writer.Write("controlled-profile-fixture");
        writer.Write(deps.Offset); writer.Write(deps.Size);
        writer.Write(config.Offset + (fault == "config" ? 1 : 0)); writer.Write(config.Size); writer.Write(0ul);
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var position = fault == "outside" && index == 0 ? 0 : fault == "overlap" && index == 1 ? entries[0].Offset : entry.Offset;
            writer.Write(position); writer.Write(entry.Size); writer.Write(fault == "compressed" && index == 0 ? 1L : 0L);
            writer.Write(entry.Kind);
            writer.Write(fault == "duplicate" && index == 1 ? entries[0].Name.ToUpperInvariant() :
                fault == "path" && index == 0 ? "../outside.dll" : entry.Name);
        }
        var result = stream.ToArray();
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(found - 8, 8), offset);
        if (fault == "marker") result[found] = 0;
        return fault == "truncated" ? result[..^2] : result;
    }
}
