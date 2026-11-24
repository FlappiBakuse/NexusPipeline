using NexusPipeline.Platform.Storage;
using System.Reflection.PortableExecutable;
using System.Runtime.Versioning;
using System.Security.Cryptography;

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
    private readonly long _length;
    private bool _disposed;

    private ProcessIdentityLease(string path, StableDirectoryLease directory, FileStream image, PcTargetImage? expected)
    {
        _path = path; _directory = directory; _image = image;
        _length = image.Length;
        _identity = PhysicalObjectIdentity.Read(image.SafeFileHandle);
        if (PhysicalObjectIdentity.Read(path) != _identity) throw new IOException("process_image_identity_changed");
        if (expected is not null && (!StringComparer.OrdinalIgnoreCase.Equals(path, expected.Path) ||
            _length != expected.SizeBytes || _identity != expected.Identity)) throw new IOException("process_image_identity_changed");
        using var timing = StartupProfile.Measure("process.image-hash");
        _sha256 = Convert.ToHexStringLower(SHA256.HashData(image));
        if (expected is not null && _sha256 != expected.Sha256) throw new IOException("process_image_identity_changed");
    }

    internal static ProcessIdentityLease HoldCurrent()
    {
        using var process = JobNative.OpenProcess(0x1000, false, Environment.ProcessId);
        var metadata = ProcessIdentityReader.ReadMetadata(process, Environment.ProcessId);
        return HoldImage(metadata.ImagePath);
    }

    internal static ProcessIdentityLease HoldImage(string path) => HoldImage(path, null);

    internal static ProcessIdentityLease HoldImage(PcTargetImage expected) => HoldImage(expected.Path, expected);

    private static ProcessIdentityLease HoldImage(string path, PcTargetImage? expected)
    {
        path = PhysicalPathPolicy.NormalizeWithoutLinks(path);
        var directory = StableDirectoryLease.Acquire(Path.GetDirectoryName(path)!);
        try
        {
            var image = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try { return new(path, directory, image, expected); }
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

    internal bool HasConsoleSubsystem()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _image.Position = 0;
            using var reader = new PEReader(_image, PEStreamOptions.LeaveOpen);
            return reader.PEHeaders.PEHeader?.Subsystem == Subsystem.WindowsCui;
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
                _image.Length != _length || PhysicalObjectIdentity.Read(_image.SafeFileHandle) != _identity ||
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

[SupportedOSPlatform("windows")]
internal sealed class ProcessIdentityScope : IDisposable
{
    [ThreadStatic] private static ProcessIdentityScope? _current;
    private readonly ProcessIdentityScope? _previous = _current;
    private readonly ProcessIdentityLease _identity;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private bool _disposed;

    internal ProcessIdentityScope(ProcessIdentityLease identity)
    { _identity = identity; _current = this; }

    internal static ProcessIdentity? ReadCurrent() => _current?._identity.ReadCurrent();

    public void Dispose()
    {
        if (_disposed) return;
        if (_thread != Environment.CurrentManagedThreadId || !ReferenceEquals(_current, this))
            throw new InvalidOperationException("process_identity_scope_order");
        _disposed = true; _current = _previous;
    }
}
