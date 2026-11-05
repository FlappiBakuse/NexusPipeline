namespace NexusPipeline.Platform.Storage.Maintenance;

internal sealed record MaintenanceEntryArguments(string RuntimeRoot, string InstalledRoot, Guid OperationId,
    Guid HandoffId, string PipeName, string JobName)
{
    internal static MaintenanceEntryArguments Parse(string[] args, string command)
    {
        if (args is not [var verb, "--runtime-root", var runtime, "--installed-root", var installed,
            "--operation-id", var operation, "--handoff-id", var handoff, "--pipe", var pipe, "--job", var job] || verb != command ||
            !Guid.TryParseExact(operation, "D", out var operationId) || operationId == Guid.Empty ||
            !Guid.TryParseExact(handoff, "D", out var handoffId) || handoffId == Guid.Empty ||
            pipe is not { Length: >= 1 and <= 180 } || pipe.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')) ||
            job.Length != 50 || !job.StartsWith("Local\\NexusWorker-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(job[18..], "N", out _)) throw new ArgumentException("maintenance_arguments_invalid");
        return new(PhysicalPathPolicy.NormalizeWithoutLinks(runtime), PhysicalPathPolicy.NormalizeWithoutLinks(installed),
            operationId, handoffId, pipe, job);
    }

    internal string[] ToArguments(string command) => [command, "--runtime-root", RuntimeRoot, "--installed-root", InstalledRoot,
        "--operation-id", OperationId.ToString("D"), "--handoff-id", HandoffId.ToString("D"), "--pipe", PipeName, "--job", JobName];
}
