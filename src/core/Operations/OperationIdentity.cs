namespace NexusPipeline.Core.Operations;

public sealed record OperationIdentity
{
    public Guid OperationId { get; }
    public string SubjectId { get; }
    public string RequestDigest { get; }
    public string Kind { get; }

    public OperationIdentity(Guid operationId, string subjectId, string requestDigest, string kind)
    {
        if (operationId == Guid.Empty || string.IsNullOrWhiteSpace(subjectId) || subjectId.Length > 256 ||
            string.IsNullOrWhiteSpace(kind) || kind.Length > 120 || requestDigest.Length != 64 ||
            requestDigest.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("Invalid accepted operation identity");
        OperationId = operationId;
        SubjectId = subjectId;
        RequestDigest = requestDigest;
        Kind = kind;
    }
}

public sealed record OperationAcceptance(Guid OperationId, bool IsNew, string Status);

public sealed class OperationConflictException() : Exception("operation_conflict");
