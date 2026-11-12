using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NexusPipeline.Core.Operations;

namespace NexusPipeline.Platform.Storage.Extensions;

internal static class ExtensionWorkRequest
{
    internal static Guid PageOperation(Guid instance, Guid producer, Guid? previous) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"nexus.extension.work-page/1/{instance:D}/{producer:D}/{previous:D}")).AsSpan(0, 16));

    internal static OperationIdentity Identity(RuntimeInstance instance, Guid id, string subject, Guid producer,
        BlobReference plan, Guid? previous, BlobReference? previousPlan)
    {
        ExtensionWorkCodec.Reference(plan);
        if (producer == Guid.Empty || (previous is null) != (previousPlan is null)) throw new IOException("extension_work_request_unbound");
        if (previousPlan is not null) ExtensionWorkCodec.Reference(previousPlan);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Format = "nexus.extension.work-request", Version = 1, instance.InstanceId,
            Producer = producer, Plan = plan, Previous = previous, PreviousPlan = previousPlan
        });
        return new(id, subject, Convert.ToHexStringLower(SHA256.HashData(bytes)), "file.cleanup");
    }
}
