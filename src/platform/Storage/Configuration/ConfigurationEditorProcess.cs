using System.Runtime.Versioning;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

[SupportedOSPlatform("windows")]
public sealed class ConfigurationEditorProcess : IDisposable
{
    private readonly ConfigurationWriterProcess _writer;

    public ConfigurationEditorProcess(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease,
        Guid sessionId, string subject)
    {
        lease.VerifyHeld(instance);
        _writer = new(instance, lease, new EditWriterScope(instance, database, contracts, sessionId, subject));
    }

    public OwnedWorkerIdentity Start() => _writer.Start();
    public OwnedWorkerIdentity Start(string executable, string expectedSha256, IReadOnlyList<string> arguments, string workingDirectory) =>
        _writer.Start(executable, expectedSha256, arguments, workingDirectory);
    public Task<StoppedConfigurationEditor> StopAsync(TimeSpan timeout) => _writer.StopAsync(timeout);
    public void Dispose() => _writer.Dispose();
}
