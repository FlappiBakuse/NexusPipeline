namespace NexusPipeline.Modules.Users.Contracts;

/// <summary>Configuration/plugin cleanup port used by the Users module.</summary>
internal interface IUserConfigDataMaintenance
{
    void RemoveUserData(string userId);

    void RemoveUserScriptData(string userId, string scriptId);
}
