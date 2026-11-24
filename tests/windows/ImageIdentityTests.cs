using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using NexusPipeline.Platform.Processes;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class ImageIdentityTests
{
    [Fact(DisplayName = "Native Registration Accepts The Current Process Identity Or Pid")]
    public void BindsCurrentIdentity()
    {
        var expected = ProcessIdentityReader.Read(Environment.ProcessId);
        using var binding = new NexusPipeline.Platform.Networking.NativeClientBinding(expected);
        Assert.True(binding.IsLive);
        using var byProcess = new NexusPipeline.Platform.Networking.NativeClientBinding(Environment.ProcessId);
        Assert.True(byProcess.IsLive);
    }

    [Fact(DisplayName = "Activation Identity Protection Retains The Current Process Identity And Rejects Reads After Release")]
    public void PreservesCurrentIdentity()
    {
        using var process = Process.GetCurrentProcess();
        using var user = WindowsIdentity.GetCurrent();
        var imagePath = process.MainModule!.FileName;
        using var image = File.OpenRead(imagePath);
        var digest = Convert.ToHexStringLower(SHA256.HashData(image));
        void RequireCurrent(ProcessIdentity actual)
        {
            Assert.Equal(process.Id, actual.ProcessId);
            Assert.Equal(process.StartTime.ToUniversalTime(), actual.CreatedAtUtc);
            Assert.Equal(imagePath, actual.ImagePath, ignoreCase: true);
            Assert.Equal(user.User!.Value, actual.UserSid);
            Assert.Equal(digest, actual.ImageSha256);
        }
        var held = ProcessIdentityLease.HoldCurrent();
        try
        {
            RequireCurrent(held.ReadCurrent());
        }
        finally { held.Dispose(); }
        Assert.Throws<ObjectDisposedException>(() => held.ReadCurrent());
        using var reopened = ProcessIdentityLease.HoldCurrent();
        RequireCurrent(reopened.ReadCurrent());
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
