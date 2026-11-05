using System.Runtime.Versioning;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Secrets;

[SupportedOSPlatform("windows")]
internal sealed class PreparedDocumentCommit : IDisposable
{
    internal BoundSecretDocument Source { get; }
    internal byte[] Content { get; }
    internal Dictionary<Guid, PreparedSecretCommit> Children { get; } = [];
    internal List<SecretFieldRetirement> Retirements { get; } = [];

    internal PreparedDocumentCommit(RuntimeInstance instance, StateDatabase database, ContractReader contracts, BoundSecretDocument source)
    {
        Source = new SecretDocumentFormat(instance, contracts).Read(source.Reference);
        Content = new SecretDocumentFormat(instance, contracts).ReadContent(Source.Document);
        try
        {
            foreach (var change in Source.Document.Secrets)
            {
                if (change.Preparation is { } preparation)
                    Children.Add(change.OperationId, PreparedSecretCommit.Read(instance, database, contracts, preparation));
                if (change.Retirement is { } retirement)
                    Retirements.Add(SecretFieldRetirement.FromDescription(instance, change.OperationId, change.Before.SecretId!.Value, retirement));
            }
        }
        catch { Dispose(); throw; }
    }

    public void Dispose()
    {
        foreach (var child in Children.Values) child.Dispose();
        foreach (var retirement in Retirements) retirement.Dispose();
    }
}
