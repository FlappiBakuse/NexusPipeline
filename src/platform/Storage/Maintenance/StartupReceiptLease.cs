using NexusPipeline.Platform.Processes;
using System.Runtime.Versioning;

namespace NexusPipeline.Platform.Storage.Maintenance;

[SupportedOSPlatform("windows")]
internal sealed class StartupReceiptLease : IDisposable
{
    private readonly MaintenanceStartupInputs _inputs;
    private FileStream? _receipt;
    internal BlobReference Reference { get; }
    internal Guid StartupGeneration { get; }
    internal ProcessIdentity Host { get; }
    internal string JobName { get; }

    internal StartupReceiptLease(MaintenanceStartupInputs inputs, FileStream receipt, BlobReference reference,
        Guid startupGeneration, ProcessIdentity host, string jobName)
    { _inputs = inputs; _receipt = receipt; Reference = reference; StartupGeneration = startupGeneration; Host = host; JobName = jobName; }

    internal void RequireCurrent(Sqlite.StateDatabase database, MaintenanceStartupBinding binding)
    {
        if (_receipt is not { CanRead: true }) throw new IOException("maintenance_startup_receipt_closed");
        _inputs.RequireCurrent(database, binding);
    }

    public void Dispose()
    {
        try { Interlocked.Exchange(ref _receipt, null)?.Dispose(); } finally { _inputs.Dispose(); }
    }
}
