using NexusPipeline.Platform.Processes;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class ImageIdentityTests
{
    [Fact(DisplayName = "Native Registration Validates The Pinned Executable Against The Supplied Identity And Rejects Mismatches")]
    public void ValidatesNativeIdentity()
    {
        var expected = ProcessIdentityReader.Read(Environment.ProcessId);
        using var binding = new NexusPipeline.Platform.Networking.NativeClientBinding(expected);
        Assert.True(binding.IsLive);
        Assert.Throws<IOException>(() => new NexusPipeline.Platform.Networking.NativeClientBinding(expected with { ImageSha256 = new string('0', 64) }));
        Assert.Throws<IOException>(() => new NexusPipeline.Platform.Networking.NativeClientBinding(expected with { CreatedAtUtc = expected.CreatedAtUtc.AddSeconds(-1) }));
        using var byProcess = new NexusPipeline.Platform.Networking.NativeClientBinding(Environment.ProcessId);
        Assert.True(byProcess.IsLive);
    }

    [Fact(DisplayName = "Activation Identity Protection Retains The Current Process Identity And Rejects Reads After Release")]
    public void PreservesCurrentIdentity()
    {
        var expected = ProcessIdentityReader.Read(Environment.ProcessId);
        var held = ProcessIdentityLease.HoldCurrent();
        try
        {
            Assert.Equal(expected, held.ReadCurrent());
            Assert.Equal(expected, held.ReadCurrent());
        }
        finally { held.Dispose(); }
        Assert.Throws<ObjectDisposedException>(() => held.ReadCurrent());
        using var reopened = ProcessIdentityLease.HoldCurrent();
        Assert.Equal(expected, reopened.ReadCurrent());
    }

    [Fact(DisplayName = "Activation Image Bytes And Parent Names Remain Protected Until Release And A Different Image Cannot Identify The Executor")]
    public void ProtectsImageLifetime()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "identity-image-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var image = Path.Combine(root, "image.exe");
        File.WriteAllBytes(image, [1, 2, 3]);
        using (var held = ProcessIdentityLease.HoldImage(image))
        {
            Assert.Throws<IOException>(() => File.WriteAllBytes(image, [3, 2, 1]));
            Assert.Throws<IOException>(() => File.Delete(image));
            Assert.Throws<IOException>(() => Directory.Move(root, root + "-moved"));
            Assert.Throws<IOException>(() => held.ReadCurrent());
        }
        File.WriteAllBytes(image, [3, 2, 1]);
        Assert.Equal(new byte[] { 3, 2, 1 }, File.ReadAllBytes(image));
        Directory.Move(root, root + "-moved");
    }
}
