using System.Security.Cryptography;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage;

internal sealed class BlobReadStream : FileStream
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, HeldContent> Held = new(StringComparer.OrdinalIgnoreCase);
    private static long _parsedBytes;
    private HeldContent? _content;

    private BlobReadStream(string path) : base(OpenLeaf(path), FileAccess.Read) { }

    private static SafeFileHandle OpenLeaf(string path)
    {
        // Opening the leaf itself prevents a replacement link from being followed before handle validation.
        var handle = CreateFile(PhysicalPathPolicy.NativePath(path), 0x80000000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = new Win32Exception(Marshal.GetLastWin32Error());
        handle.Dispose();
        throw new IOException("immutable_blob_open_failed", error);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    internal static FileStream Open(string path, BlobReference reference, bool retainInScope)
    {
        var reader = new BlobReadStream(path);
        try
        {
            var identity = PhysicalObjectIdentity.Read(reader.SafeFileHandle);
            if (reader.Length != reference.SizeBytes) throw Invalid();
            lock (Gate)
                if (Held.TryGetValue(path, out var prior))
                {
                    reader.Attach(prior, identity, reference);
                    if (retainInScope) BlobReadScope.Track(path);
                    return reader;
                }
            if (Convert.ToHexStringLower(SHA256.HashData(reader)) != reference.Sha256) throw Invalid();
            reader.Position = 0;
            var anchor = new FileStream(OpenLeaf(path), FileAccess.Read);
            try
            {
                if (PhysicalObjectIdentity.Read(anchor.SafeFileHandle) != identity) throw Invalid();
                lock (Gate)
                {
                    if (!Held.TryGetValue(path, out var content))
                    {
                        content = new(path, reference, identity, anchor);
                        Held.Add(path, content);
                        anchor = null!;
                    }
                    reader.Attach(content, identity, reference);
                }
            }
            finally { anchor?.Dispose(); }
            if (retainInScope) BlobReadScope.Track(path);
            return reader;
        }
        catch { reader.Dispose(); throw; }
    }

    private void Attach(HeldContent content, PhysicalObjectIdentity identity, BlobReference reference)
    {
        if (content.Identity != identity || content.Reference != reference) throw Invalid();
        content.Readers++;
        _content = content;
    }

    private void Release()
    {
        var content = Interlocked.Exchange(ref _content, null);
        if (content is null) return;
        Release(content);
    }

    internal static IDisposable Retain(string path) => Retain(path, out _);

    internal static FileTreeSnapshot ReadSnapshot(FileStream input, Func<FileTreeSnapshot> read)
        => input is BlobReadStream { _content: { } content } && content.Reference.SizeBytes <= 4096
            ? content.ReadSnapshot(read) : read();

    internal static T ReadEvidence<T>(FileStream input, Func<T> read) where T : class
    {
        if (typeof(T) != typeof(FrozenJobClosure) && typeof(T) != typeof(StepEvidenceDocument) && typeof(T) != typeof(RawOutputManifest))
            throw new ArgumentException("blob_evidence_type_not_immutable");
        return input is BlobReadStream { _content: { } content } && content.Reference.SizeBytes <= 65536
            ? content.ReadEvidence(read) : read();
    }

    internal static IDisposable Retain(string path, out BlobReference reference)
    {
        lock (Gate)
        {
            if (!Held.TryGetValue(path, out var content)) throw Invalid();
            reference = content.Reference;
            content.Readers++;
            return new RetainedContent(content);
        }
    }

    private sealed class RetainedContent(HeldContent content) : IDisposable
    {
        private HeldContent? _content = content;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _content, null) is { } held) Release(held);
        }
    }

    private static void Release(HeldContent content)
    {
        lock (Gate)
            if (--content.Readers == 0)
            {
                Held.Remove(content.Path);
                _parsedBytes -= content.ParsedBytes;
                content.Anchor.Dispose();
            }
    }

    protected override void Dispose(bool disposing)
    {
        try { base.Dispose(disposing); }
        finally { Release(); }
    }

    public override async ValueTask DisposeAsync()
    {
        try { await base.DisposeAsync().ConfigureAwait(false); }
        finally { Release(); GC.SuppressFinalize(this); }
    }

    ~BlobReadStream() => Dispose(false);

    // The private handle denies writes and deletion even if a consumer closes its exposed SafeFileHandle.
    private sealed class HeldContent(string path, BlobReference reference, PhysicalObjectIdentity identity, FileStream anchor)
    {
        private FileTreeSnapshot? _snapshot;
        private object? _evidence;
        internal long ParsedBytes { get; private set; }
        internal string Path { get; } = path;
        internal BlobReference Reference { get; } = reference;
        internal PhysicalObjectIdentity Identity { get; } = identity;
        internal FileStream Anchor { get; } = anchor;
        internal int Readers;
        internal FileTreeSnapshot ReadSnapshot(Func<FileTreeSnapshot> read)
        {
            lock (this) return _snapshot ??= read();
        }

        internal T ReadEvidence<T>(Func<T> read) where T : class
        {
            lock (this)
            {
                if (_evidence is T saved) return saved;
                var value = read();
                var bytes = 256 + Reference.SizeBytes * 8;
                lock (Gate)
                    if (_evidence is null && bytes <= 8388608 - _parsedBytes)
                    { _evidence = value; ParsedBytes = bytes; _parsedBytes += bytes; }
                return value;
            }
        }
    }

    private static IOException Invalid() => new("immutable_blob_mismatch");
}
