namespace NexusPipeline.Platform.Storage.Automation;

public sealed record AutomationExportAttachment(Guid Id, string PackagePath, long SizeBytes, string Sha256);

public sealed class PreparedAutomationExport : IDisposable
{
    private readonly byte[] _bundle;
    private Dictionary<Guid, FileStream>? _files;
    public IReadOnlyList<AutomationExportAttachment> Attachments { get; }

    internal PreparedAutomationExport(byte[] bundle, IReadOnlyList<AutomationExportAttachment> attachments, Dictionary<Guid, FileStream> files)
    {
        _bundle = bundle.ToArray();
        Attachments = Array.AsReadOnly(attachments.ToArray());
        _files = files;
    }

    public byte[] ExportBundle()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _files) is null, this);
        return _bundle.ToArray();
    }

    public void CopyAttachmentTo(Guid assetId, Stream destination, CancellationToken cancellationToken = default)
    {
        var files = Volatile.Read(ref _files) ?? throw new ObjectDisposedException(nameof(PreparedAutomationExport));
        if (!files.TryGetValue(assetId, out var input)) throw new ArgumentException("automation_export_attachment_unbound", nameof(assetId));
        var size = Attachments.Single(value => value.Id == assetId).SizeBytes;
        var buffer = new byte[65536];
        long offset = 0;
        while (offset < size)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = RandomAccess.Read(input.SafeFileHandle, buffer.AsSpan(0, checked((int)Math.Min(buffer.Length, size - offset))), offset);
            if (count == 0) throw new IOException("automation_export_content_changed");
            destination.Write(buffer, 0, count);
            offset += count;
        }
    }

    public async Task CopyAttachmentToAsync(Guid assetId, Stream destination, CancellationToken cancellationToken = default)
    {
        var files = Volatile.Read(ref _files) ?? throw new ObjectDisposedException(nameof(PreparedAutomationExport));
        if (!files.TryGetValue(assetId, out var input)) throw new ArgumentException("automation_export_attachment_unbound", nameof(assetId));
        var size = Attachments.Single(value => value.Id == assetId).SizeBytes;
        var buffer = new byte[65536];
        long offset = 0;
        while (offset < size)
        {
            var count = await RandomAccess.ReadAsync(input.SafeFileHandle,
                buffer.AsMemory(0, checked((int)Math.Min(buffer.Length, size - offset))), offset, cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new IOException("automation_export_content_changed");
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            offset += count;
        }
    }

    public void Dispose()
    {
        var files = Interlocked.Exchange(ref _files, null);
        if (files is null) return;
        foreach (var file in files.Values) file.Dispose();
    }
}
