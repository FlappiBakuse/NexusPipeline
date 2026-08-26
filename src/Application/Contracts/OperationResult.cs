namespace NexusPipeline.App.Contracts;

/// <summary>应用层错误分类。适配层将其映射为 HTTP、CLI 或其他协议状态。</summary>
internal enum OperationErrorKind
{
    Validation,
    NotFound,
    Conflict,
    Forbidden,
    Unavailable,
    Timeout,
    Internal,
}

/// <summary>与传输协议无关的应用操作错误。</summary>
internal sealed record OperationError(
    string Code,
    string Message,
    OperationErrorKind Kind,
    IReadOnlyList<string>? Candidates = null);

/// <summary>应用命令/查询的统一结果容器。</summary>
internal sealed class OperationResult<T>
{
    private OperationResult(bool succeeded, T? value, OperationError? error)
    {
        Succeeded = succeeded;
        Value = value;
        Error = error;
    }

    public bool Succeeded { get; }

    public bool Success => Succeeded;

    public T? Value { get; }

    public OperationError? Error { get; }

    public string? ErrorCode => Error?.Code;

    public string? ErrorMessage => Error?.Message;

    public OperationErrorKind? ErrorKind => Error?.Kind;

    public static OperationResult<T> Ok(T value) => new(true, value, null);

    public static OperationResult<T> Failure(OperationError error) => new(false, default, error);

    public static OperationResult<T> Failure(
        string code,
        string message,
        OperationErrorKind kind,
        IReadOnlyList<string>? candidates = null)
    {
        return Failure(new OperationError(code, message, kind, candidates));
    }
}

internal static class OperationResult
{
    public static OperationResult<bool> Ok() => OperationResult<bool>.Ok(true);

    public static OperationResult<bool> Failure(
        string code,
        string message,
        OperationErrorKind kind,
        IReadOnlyList<string>? candidates = null)
    {
        return OperationResult<bool>.Failure(code, message, kind, candidates);
    }
}
