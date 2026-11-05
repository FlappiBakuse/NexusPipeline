using System.Runtime.Versioning;
using System.IO.Compression;
using System.Text.Json;
using NexusPipeline.Core.Control;
using NexusPipeline.Core.Records;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Host.Lifecycle;
using NexusPipeline.Host.Control.Notifications;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Notifications;

namespace NexusPipeline.Host.Control.Runtime;

[SupportedOSPlatform("windows")]
internal sealed class RuntimeUseCases(IRuntimeDoctor doctor, ExecutionRuntimeConsumer execution, NotificationRuntime notifications,
    HostAutomaticUpdates automaticUpdates, NexusPipeline.Platform.Storage.Scheduling.PersistentScheduleService scheduling, RuntimeChecks checks)
{
    internal RuntimeDoctorReport Doctor(AuthenticatedControlRequest request, CancellationToken cancellation = default)
    {
        request.Require(ControlCapability.DoctorRead);
        return checks.Read(doctor.ReadSupport(cancellation), execution.FailureCode is null,
            scheduling.IsRunning && scheduling.FailureCode is null, automaticUpdates.Status.FailureCode is null);
    }

    internal byte[] Support(AuthenticatedControlRequest request, CancellationToken cancellation = default)
    {
        var report = Doctor(request, cancellation);
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write("manifest.json", new
            {
                format = "nexus.diagnostic.support", version = 1,
                hostVersion = "0.18.0", sdkVersion = "0.1.0",
                report.ReportId, report.InstanceId, report.CapturedAtUtc,
                contents = new[] { "doctor.json", "environment.json", "plugins.json", "runtime-state.json", "safe-log.json" },
                safeLogTail = "At most 64 typed completion facts; no worker text or private reason codes"
            });
            Write("doctor.json", report);
            Write("environment.json", RuntimeChecks.EnvironmentSummary());
            Write("plugins.json", report.Details!.Extensions);
            Write("runtime-state.json", new { report.Admission, report.RecoveryRevision, report.IsolatedResourceCount,
                report.HeldResourceCount, report.PendingOperationCount, report.AttemptReceiptCount, report.CompletedRunCount,
                report.Details.OpenConfigurationCount, report.Details.PendingExtensionCount, report.Details.PendingUpdateCount,
                report.Details.DefinedQueueCount, report.Details.WaitingOccurrenceCount });
            Write("safe-log.json", report.Details.SafeLogTail);

            void Write(string name, object value)
            {
                cancellation.ThrowIfCancellationRequested();
                using var entry = archive.CreateEntry(name, CompressionLevel.Fastest).Open();
                JsonSerializer.Serialize(entry, value, NotificationUseCases.Json);
            }
        }
        if (buffer.Length > 65536) throw new IOException("diagnostic_support_limit");
        return buffer.ToArray();
    }

    internal object Status(AuthenticatedControlRequest request, CancellationToken cancellation = default, int? currentWebPort = null)
    {
        request.Require(ControlCapability.StatusRead);
        var report = doctor.Read(cancellation);
        return new
        {
            format = "nexus.control.status", version = 1, report.InstanceId,
            processId = Environment.ProcessId,
            currentWebPort,
            accessAddresses = currentWebPort is { } port ? NexusPipeline.Platform.Networking.NetworkAccessAddresses.Read(port) : null,
            hostVersion = "0.18.0", sdkVersion = "0.1.0",
            admission = report.Admission, recoveryRevision = report.RecoveryRevision,
            pendingOperations = report.PendingOperationCount,
            execution = new { execution.FailureCode, execution.PendingReconciliationCount },
            notifications = new { notifications.FailureCode },
            scheduling = new { scheduling.IsRunning, scheduling.FailureCode, scheduling.Diagnostics },
            updates = automaticUpdates.Status
        };
    }
}
