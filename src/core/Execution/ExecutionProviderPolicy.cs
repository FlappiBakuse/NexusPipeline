using System.Collections.Immutable;
using NexusPipeline.Core.Extensions;

namespace NexusPipeline.Core.Execution;

public sealed record ExecutionProviderDeclaration(string EntryId, string WorkerEntry,
    ImmutableArray<string> Arguments, ProviderLauncherDeclaration? Launcher)
{
    public bool GracefulStop { get; init; }
    public bool TargetControl { get; init; }
    public ProviderRetryDeclaration? Retry { get; init; }
    public ImmutableArray<ProviderConfigurationDeclaration> ConfigurationResources { get; init; } = [];
}

public sealed record ProviderRetryDeclaration(RetrySafety Safety, string Scope, string IdempotencyProtocol,
    ImmutableArray<AttemptOutcome> Outcomes);

public sealed record ProviderConfigurationDeclaration(string ResourceId, int DeclarationIndex, long DefinitionRevision);

public sealed record ProviderLauncherDeclaration(string Entry, ImmutableArray<string> Arguments,
    bool WritesManagedConfiguration, string TerminalProtocol);

public static class ExecutionProviderPolicy
{
    public const string Protocol = "nexus.execution.provider/1";
    public const string LauncherTerminalProtocol = "worker-terminal/1";

    public static ExecutionProviderDeclaration Freeze(ExecutionProviderDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        if (declaration.TargetControl && !declaration.GracefulStop) throw new ArgumentException("execution_provider_target_stop_required");
        Identifier(declaration.EntryId);
        Entry(declaration.WorkerEntry);
        var arguments = Arguments(declaration.Arguments);
        ProviderLauncherDeclaration? launcher = null;
        if (declaration.Launcher is { } input)
        {
            Entry(input.Entry);
            if (input.WritesManagedConfiguration || input.TerminalProtocol != LauncherTerminalProtocol)
                throw new ArgumentException("execution_provider_launcher_authority_invalid");
            launcher = input with { Arguments = Arguments(input.Arguments) };
        }
        var resources = declaration.ConfigurationResources;
        if (resources.IsDefault || resources.Length > 32 || resources.Any(resource => resource is null ||
            resource.DeclarationIndex is < 0 or > 31 || resource.DefinitionRevision is < 1 or > 9007199254740991) ||
            resources.Select(resource => resource.ResourceId).Distinct(StringComparer.Ordinal).Count() != resources.Length ||
            resources.Select(resource => resource.DeclarationIndex).Distinct().Count() != resources.Length)
            throw new ArgumentException("execution_provider_configuration_declaration_invalid");
        foreach (var resource in resources) Identifier(resource.ResourceId);
        return declaration with { Arguments = arguments, Launcher = launcher, ConfigurationResources = resources.ToArray().ToImmutableArray(), Retry = FreezeRetry(declaration.Retry) };
    }

    public static ProviderRetryDeclaration? FreezeRetry(ProviderRetryDeclaration? declaration)
    {
        if (declaration is null) return null;
        if (declaration.Safety != RetrySafety.Safe || declaration.Scope != "workerAndUpstream" ||
            declaration.IdempotencyProtocol != "stable-run-binding/1" || declaration.Outcomes.IsDefault ||
            declaration.Outcomes.Length != 2 || !declaration.Outcomes.Contains(AttemptOutcome.Failed) ||
            !declaration.Outcomes.Contains(AttemptOutcome.Interrupted))
            throw new ArgumentException("execution_provider_retry_authority_invalid");
        return declaration with { Outcomes = [AttemptOutcome.Failed, AttemptOutcome.Interrupted] };
    }

    public static void RequireExtensionId(string value) => ExtensionIdentifiers.Require(value);

    private static ImmutableArray<string> Arguments(ImmutableArray<string> arguments)
    {
        if (arguments.IsDefault || arguments.Length > 128 ||
            arguments.Any(value => value is null || value.Length > 4096 || value.Contains('\0')))
            throw new ArgumentException("execution_provider_arguments_invalid");
        return arguments.ToArray().ToImmutableArray();
    }

    private static void Identifier(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 120 || value[0] is not (>= 'a' and <= 'z') ||
            value.Any(character => character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.')))
            throw new ArgumentException("execution_provider_entry_invalid");
    }

    private static void Entry(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 1024 || value.Contains('\\') || value.Contains(':') ||
            value.Split('/').Any(part => part.Length == 0 || part is "." or ".." ||
                part.EndsWith(' ') || part.EndsWith('.') || part.Any(char.IsControl)) ||
            !value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("execution_provider_worker_path_invalid");
    }
}
