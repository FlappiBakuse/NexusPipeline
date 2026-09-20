using NexusPipeline.Modules.Users;
using NexusPipeline.Platform.Storage;
namespace NexusPipeline.Modules.Users.Persistence;


/// <summary>数据持久化仓储：脚本、队列与全局用户的 JSON 读写。运行时数据（config/）集中于此层。</summary>
internal static class UserDefinitionStore
{

    public static List<NexusUser> LoadUsers()
    {
        return JsonStore.LoadList<NexusUser>(AppPaths.UsersPath);
    }

    public static void SaveUsers(IEnumerable<NexusUser> users)
    {
        JsonStore.SaveList(AppPaths.UsersPath, users.ToList());
    }
}
