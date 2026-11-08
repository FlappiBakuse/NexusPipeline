using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Platform.Processes;

[SupportedOSPlatform("windows")]
internal sealed class ProcessIdentityLease : IDisposable
{
    private readonly object _gate = new();
    private readonly StableDirectoryLease _directory;
    private readonly FileStream _image;
    private readonly PhysicalObjectIdentity _identity;
    private readonly string _path;
    private readonly string _sha256;
    private bool _disposed;

    private ProcessIdentityLease(string path, StableDirectoryLease directory, FileStream image)
    {
        _path = path; _directory = directory; _image = image;
        _identity = PhysicalObjectIdentity.Read(image.SafeFileHandle);
        if (PhysicalObjectIdentity.Read(path) != _identity) throw new IOException("process_image_identity_changed");
        using var timing = StartupProfile.Measure("process.image-hash");
        _sha256 = Convert.ToHexStringLower(SHA256.HashData(image));
    }

    internal static ProcessIdentityLease HoldCurrent()
    {
        using var process = JobNative.OpenProcess(0x1000, false, Environment.ProcessId);
        var metadata = ProcessIdentityReader.ReadMetadata(process, Environment.ProcessId);
        return HoldImage(metadata.ImagePath);
    }

    internal static ProcessIdentityLease HoldImage(string path)
    {
        path = PhysicalPathPolicy.NormalizeWithoutLinks(path);
        var directory = StableDirectoryLease.Acquire(Path.GetDirectoryName(path)!);
        try
        {
            var image = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try { return new(path, directory, image); }
            catch { image.Dispose(); throw; }
        }
        catch { directory.Dispose(); throw; }
    }

    internal string Sha256
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _sha256;
            }
        }
    }

    internal ProcessIdentity ReadCurrent()
    {
        using var timing = StartupProfile.Measure("writer.identity");
        using var process = JobNative.OpenProcess(0x1000, false, Environment.ProcessId);
        return Read(process, Environment.ProcessId);
    }

    internal ProcessIdentity Read(SafeKernelHandle process, int processId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var metadata = ProcessIdentityReader.ReadMetadata(process, processId);
            if (!StringComparer.OrdinalIgnoreCase.Equals(metadata.ImagePath, _path) ||
                PhysicalObjectIdentity.Read(_path) != _identity) throw new IOException("process_image_identity_changed");
            return new(metadata.ProcessId, metadata.CreatedAtUtc, metadata.ImagePath, _sha256, metadata.UserSid, metadata.IsElevated);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { _image.Dispose(); } finally { _directory.Dispose(); }
        }
    }
}
