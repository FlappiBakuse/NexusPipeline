namespace NexusPipeline.Platform.Storage;

internal sealed class PhysicalDirectoryLease : IDisposable
{
    private readonly PhysicalDirectoryHandle _handle;
    internal string Path { get; }
    internal PhysicalObjectIdentity Identity { get; }

    internal PhysicalDirectoryLease(string path, PhysicalObjectIdentity expected)
    {
        Path = PhysicalPathPolicy.NormalizeWithoutLinks(path);
        if (!Directory.Exists(Path)) throw new IOException("leased_directory_missing");
        _handle = PhysicalDirectoryHandle.Open(Path, expected);
        try
        {
            Identity = PhysicalObjectIdentity.Read(Path);
            if (Identity != expected || PhysicalObjectIdentity.Read(Path) != expected) throw new IOException("leased_directory_identity_changed");
        }
        catch { _handle.Dispose(); throw; }
    }

    public void Dispose() => _handle.Dispose();
}
