using System.Globalization;
using System.Text;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record ExecutionLogSelector(ExecutionPlanLogTarget Target, string Declaration)
{
    internal string Expand(DateTimeOffset sampledAt)
    {
        if (Target.TimeZoneId is null) return Declaration;
        var local = TimeZoneInfo.ConvertTime(sampledAt, TimeZoneInfo.FindSystemTimeZoneById(Target.TimeZoneId));
        var result = new StringBuilder();
        for (var index = 0; index < Declaration.Length; index++)
        {
            if (Declaration[index] != '{') { result.Append(Declaration[index]); continue; }
            var end = Declaration.IndexOf('}', index + 1);
            var token = Declaration[(index + 1)..end].Replace('Y', 'y').Replace('D', 'd');
            result.Append(local.ToString(token, CultureInfo.InvariantCulture)); index = end;
        }
        var expanded = Path.GetFullPath(result.ToString());
        LogBaselineCodec.RequirePath(Target.ScopePath, PhysicalPathPolicy.NormalizeSyntax(expanded.Replace("*", "x", StringComparison.Ordinal)));
        return expanded;
    }

    internal bool Potential(string path)
    {
        var pattern = new StringBuilder();
        for (var index = 0; index < Declaration.Length; index++)
        {
            if (Declaration[index] != '{') { pattern.Append(Declaration[index]); continue; }
            var end = Declaration.IndexOf('}', index + 1);
            var rendered = DateTimeOffset.UnixEpoch.ToString(Declaration[(index + 1)..end].Replace('Y', 'y').Replace('D', 'd'), CultureInfo.InvariantCulture);
            pattern.Append(string.Join(Path.DirectorySeparatorChar.ToString(), rendered.Split(['/','\\']).Select(_ => "*"))); index = end;
        }
        var parts = pattern.ToString().Split(Path.DirectorySeparatorChar); var actual = path.Split(Path.DirectorySeparatorChar);
        return parts.Length == actual.Length && parts.Zip(actual).All(pair =>
            System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pair.First, pair.Second, ignoreCase: true));
    }
}

internal static class ExecutionLogSelectors
{
    internal const int MaximumCandidates = 4096;

    internal static ExecutionPlanLogTarget Capture(int index, string declaration)
    {
        var syntax = Parse(declaration);
        var target = new ExecutionPlanLogTarget
        {
            DeclarationIndex = index, ScopePath = syntax.Scope,
            Selector = syntax.Dynamic ? "latestMatchingFile" : Directory.Exists(syntax.Scope) ? "latestFile" : "file",
            TimeZoneId = syntax.Dates ? TimeZoneInfo.Local.Id : null
        };
        Require(target, index, declaration); return target;
    }

    internal static void Require(ExecutionPlanLogTarget target, int index, string declaration)
    {
        var syntax = Parse(declaration);
        if (target.DeclarationIndex != index || target.ScopePath != syntax.Scope ||
            target.Selector is not ("file" or "latestFile" or "latestMatchingFile") ||
            (target.Selector == "latestMatchingFile") != syntax.Dynamic || syntax.Dates != (target.TimeZoneId is not null)) throw Invalid();
        if (target.TimeZoneId is not null)
        {
            try { _ = TimeZoneInfo.FindSystemTimeZoneById(target.TimeZoneId); }
            catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException) { throw Invalid(); }
        }
        _ = new ExecutionLogSelector(target, syntax.Declaration).Expand(DateTimeOffset.UnixEpoch);
    }

    internal static IReadOnlyList<ExecutionLogSelector> Read(RuntimeInstance instance, ContractReader contracts, ExecutionPlan total, string stepId)
    {
        var step = total.Steps.Single(item => item.StepId == stepId);
        var bundle = new ExecutionJudgementInputs(instance, contracts).ReadBundle(total);
        var paths = bundle.Scripts.Single(item => item.Id == step.ScriptId).LogPaths;
        if (paths.Count != step.LogTargets.Count) throw Invalid();
        var roots = total.RootBindings.ToDictionary(item => item.RootId, item => item.Path);
        return paths.Select((path, index) =>
        {
            var declaration = Declaration(roots[path.RootId], path.Relative);
            Require(step.LogTargets[index], index, declaration);
            return new ExecutionLogSelector(step.LogTargets[index], declaration);
        }).ToArray();
    }

    internal static string Declaration(string root, string relative)
    {
        if (string.IsNullOrEmpty(relative) || relative.Split('/').Any(part => part is "" or "." or "..") ||
            relative.Any(character => char.IsControl(character) || character is ':' or '\\')) throw Invalid();
        return NormalizePattern(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar + relative);
    }

    internal static void RequireContained(string scope, string path)
    {
        var canonical = PhysicalPathPolicy.NormalizeWithoutLinks(path);
        if (!StringComparer.OrdinalIgnoreCase.Equals(scope, canonical) &&
            !canonical.StartsWith(scope.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw Invalid();
    }

    private static (string Declaration, string Scope, bool Dynamic, bool Dates) Parse(string declaration)
    {
        if (!Path.IsPathFullyQualified(declaration) || declaration.Length > 32767 || declaration.Contains('?')) throw Invalid();
        var normalized = NormalizePattern(declaration);
        var dynamicAt = normalized.IndexOfAny(['{', '*']);
        if (normalized.Contains('}') && !normalized.Contains('{')) throw Invalid();
        if (normalized.IndexOf('*') is var wildcard && wildcard >= 0 && wildcard < normalized.LastIndexOf(Path.DirectorySeparatorChar)) throw Invalid();
        var dates = false;
        for (var index = 0; index < normalized.Length; index++)
        {
            if (normalized[index] == '}') throw Invalid();
            if (normalized[index] != '{') continue;
            var end = normalized.IndexOf('}', index + 1);
            if (end < index + 2) throw Invalid();
            var token = normalized[(index + 1)..end];
            if (token.Contains('{') || token.Any(character => character is '\\' or ':' or '*' or '?' || char.IsControl(character))) throw Invalid();
            string expanded;
            try { expanded = DateTimeOffset.UnixEpoch.ToString(token.Replace('Y', 'y').Replace('D', 'd'), CultureInfo.InvariantCulture); }
            catch (FormatException) { throw Invalid(); }
            PhysicalPathPolicy.ValidateRelativePath(expanded); dates = true; index = end;
        }
        var scope = dynamicAt < 0 ? normalized : normalized[..normalized.LastIndexOf(Path.DirectorySeparatorChar, dynamicAt)];
        if (scope.EndsWith(':')) scope += Path.DirectorySeparatorChar;
        scope = PhysicalPathPolicy.NormalizeSyntax(scope);
        if (dynamicAt < 0 && scope != declaration) throw Invalid();
        return (normalized, scope, dynamicAt >= 0, dates);
    }

    private static string NormalizePattern(string value)
    {
        var tokens = new List<string>(); var masked = new StringBuilder();
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '{') { masked.Append(value[index]); continue; }
            var end = value.IndexOf('}', index + 1); if (end < 0) throw Invalid();
            var token = value[index..(end + 1)]; var marker = "__nexus_date_" + tokens.Count + "__";
            if (value.Contains(marker, StringComparison.Ordinal)) throw Invalid();
            tokens.Add(token); masked.Append(marker); index = end;
        }
        var normalized = Path.GetFullPath(masked.ToString());
        for (var index = 0; index < tokens.Count; index++) normalized = normalized.Replace("__nexus_date_" + index + "__", tokens[index], StringComparison.Ordinal);
        return normalized;
    }

    private static IOException Invalid() => new("execution_log_selector_unbound");
}
