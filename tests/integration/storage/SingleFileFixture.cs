using System.Reflection.PortableExecutable;
using System.Text;
using NexusPipeline.Platform.Storage.Maintenance;
using Xunit;

namespace NexusPipeline.StorageTests;

internal static class SingleFileFixture
{
    internal static void FailStartup(string executable)
    {
        var bytes = File.ReadAllBytes(executable);
        using var image = new PEReader(new MemoryStream(bytes));
        var extent = image.PEHeaders.SectionHeaders.Max(section => section.PointerToRawData + section.SizeOfRawData);
        var name = Encoding.UTF8.GetBytes("NexusPipeline.Host.dll");
        var position = bytes.AsSpan(0, extent).IndexOf(name);
        Assert.True(position >= 0);
        Assert.Equal(-1, bytes.AsSpan(position + name.Length, extent - position - name.Length).IndexOf(name));
        Encoding.UTF8.GetBytes("NexusPipeline.Fail.dll").CopyTo(bytes, position);
        File.WriteAllBytes(executable, bytes);
        using var input = File.OpenRead(executable);
        UpdateBuildProfile.RequireBundle(input, "NexusPipeline.Host.dll", "test");
    }
}
