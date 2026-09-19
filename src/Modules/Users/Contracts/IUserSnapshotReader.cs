using NexusPipeline.Modules.Users;

namespace NexusPipeline.Modules.Users.Contracts;

/// <summary>Typed snapshot port for consumers that validate queue composition.</summary>
internal interface IUserSnapshotReader
{
    NexusUser? FindById(string id);

    IReadOnlyList<NexusUser> Snapshot();
}
