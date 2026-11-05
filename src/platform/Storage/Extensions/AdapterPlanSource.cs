using System.Collections.Immutable;
using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage.Configuration;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed record AdapterPlanSource(IReadOnlyList<ConfigurationRevision> Configurations,
    AdapterDiscovery Discovery, ExecutionAdapter Descriptor, ExecutionPlanExtensionLocksItem Lock,
    IReadOnlyDictionary<string, byte[]> Inputs)
{
    internal ImmutableArray<ExecutionPlanExtensionLocksItem> CompilerLocks { get; init; } = [];
    internal bool ExclusiveProgram { get; init; }
    internal ExecutionProviderUse? ExecutionProvider { get; init; }
}
