using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace NexusPipeline.Platform.Processes;

public sealed record ProcessIdentity(int ProcessId, DateTime CreatedAtUtc, string ImagePath, string ImageSha256, string UserSid, bool IsElevated);
internal readonly record struct ProcessIdentityMetadata(int ProcessId, DateTime CreatedAtUtc, string ImagePath, string UserSid, bool IsElevated)
{
    internal static ProcessIdentityMetadata From(ProcessIdentity value) =>
        new(value.ProcessId, value.CreatedAtUtc, value.ImagePath, value.UserSid, value.IsElevated);
}

[SupportedOSPlatform("windows")]
public static class ProcessIdentityReader
{
    public static ProcessIdentity Read(int processId)
    {
        if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
        using var process = JobNative.OpenProcess(0x1000, false, processId);
        if (process.IsInvalid) throw Failure();
        return Read(process, processId);
    }

    internal static ProcessIdentity Read(SafeKernelHandle process, int processId)
    {
        var metadata = ReadMetadata(process, processId);
        using var file = File.OpenRead(metadata.ImagePath);
        return new(metadata.ProcessId, metadata.CreatedAtUtc, metadata.ImagePath,
            Convert.ToHexStringLower(SHA256.HashData(file)), metadata.UserSid, metadata.IsElevated);
    }

    internal static ProcessIdentityMetadata ReadMetadata(SafeKernelHandle process, int processId)
    {
        if (!JobNative.GetProcessTimes(process, out var created, out _, out _, out _)) throw Failure();
        var image = new StringBuilder(32768);
        uint length = (uint)image.Capacity;
        if (!JobNative.QueryFullProcessImageNameW(process, 0, image, ref length)) throw Failure();
        if (!JobNative.OpenProcessToken(process, 8, out var token)) throw Failure();
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        {
            if (!JobNative.GetTokenInformation(token, 20, out var elevated, sizeof(int), out _)) throw Failure();
            return new(processId, DateTime.FromFileTimeUtc(created), image.ToString(),
                identity.User?.Value ?? throw new IOException("Process user identity unavailable"), elevated != 0);
        }
    }

    private static Win32Exception Failure() => new(Marshal.GetLastPInvokeError());
}
