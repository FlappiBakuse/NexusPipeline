using NexusPipeline.Models;

namespace NexusPipeline.App;

/// <summary>统一的 ID/名称解析结果。ID 精确优先，同名对象不会静默选取第一个。</summary>
internal enum TargetResolutionKind
{
    Found,
    NotFound,
    Ambiguous,
}

internal sealed record TargetResolution<T>(
    TargetResolutionKind Kind,
    T? Value,
    IReadOnlyList<T> Candidates)
{
    public bool IsFound => Kind == TargetResolutionKind.Found;

    public static TargetResolution<T> Found(T value) => new(TargetResolutionKind.Found, value, new[] { value });

    public static TargetResolution<T> NotFound() => new(TargetResolutionKind.NotFound, default, Array.Empty<T>());

    public static TargetResolution<T> Ambiguous(IReadOnlyList<T> candidates) =>
        new(TargetResolutionKind.Ambiguous, default, candidates);
}

internal static class TargetResolver
{
    public static TargetResolution<T> Resolve<T>(
        IEnumerable<T> source,
        string? reference,
        Func<T, string> idSelector,
        Func<T, string> nameSelector)
    {
        string target = reference?.Trim() ?? "";
        if (target.Length == 0)
        {
            return TargetResolution<T>.NotFound();
        }

        List<T> items = source.ToList();
        List<T> idMatches = items
            .Where(item => string.Equals(idSelector(item), target, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (idMatches.Count == 1)
        {
            return TargetResolution<T>.Found(idMatches[0]);
        }
        if (idMatches.Count > 1)
        {
            return TargetResolution<T>.Ambiguous(idMatches);
        }

        List<T> nameMatches = items
            .Where(item => string.Equals(nameSelector(item), target, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return nameMatches.Count switch
        {
            0 => TargetResolution<T>.NotFound(),
            1 => TargetResolution<T>.Found(nameMatches[0]),
            _ => TargetResolution<T>.Ambiguous(nameMatches),
        };
    }

    public static TargetResolution<ScriptInstance> ResolveScript(
        IEnumerable<ScriptInstance> scripts,
        string? reference)
    {
        return Resolve(scripts, reference, script => script.Id, script => script.Name);
    }

    public static TargetResolution<DispatchQueue> ResolveQueue(
        IEnumerable<DispatchQueue> queues,
        string? reference)
    {
        return Resolve(queues, reference, queue => queue.Id, queue => queue.Name);
    }

    public static TargetResolution<NexusUser> ResolveUser(
        IEnumerable<NexusUser> users,
        string? reference)
    {
        return Resolve(users, reference, user => user.Id, user => user.Name);
    }
}
