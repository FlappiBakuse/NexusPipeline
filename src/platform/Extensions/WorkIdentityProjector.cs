using NexusPipeline.Core.Secrets;
using NexusPipeline.Extensibility.Scheduling;
using NexusPipeline.Extensibility.Secrets;

namespace NexusPipeline.Platform.Extensions;

internal sealed class WorkIdentityProjector(IManualWorkHandler handler)
{
    internal string? Version { get; } = handler.IdentityVersion;
    internal bool CanRotate(string field, SecretValue before, SecretValue after)
    {
        SecretDraft? first = null, second = null;
        try
        {
            before.Use(value => first = new(value)); after.Use(value => second = new(value));
            return handler.CanRotateIdentity(field, first!, second!);
        }
        finally { first?.Dispose(); second?.Dispose(); }
    }
    internal SecretValue Project(string field, SecretValue material)
    {
        SecretDraft? draft = null;
        material.Use(value => draft = new(value));
        using var source = draft ?? throw new IOException("work_identity_material_missing");
        using var projected = handler.ProjectIdentity(field, source) ?? throw new IOException("work_identity_missing");
        SecretValue? result = null;
        projected.Use(value =>
        {
            if (value.Length is < 1 or > 16384 || value.IsWhiteSpace() || value.Contains('\0')) throw new IOException("work_identity_invalid");
            result = new(value);
        });
        return result ?? throw new IOException("work_identity_missing");
    }
}
