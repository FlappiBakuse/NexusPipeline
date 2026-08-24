using NexusPipeline.Models;
namespace NexusPipeline.Persistence;

/// <summary>数据持久化仓储：脚本、队列与全局用户的 JSON 读写。运行时数据（config/）集中于此层。</summary>
internal static class DataStore
{
    public static List<ScriptInstance> LoadScripts()
    {
        return JsonStore.LoadList<ScriptInstance>(AppPaths.ScriptsPath);
    }

    public static List<DispatchQueue> LoadQueues()
    {
        return JsonStore.LoadList<DispatchQueue>(AppPaths.QueuesPath);
    }

    public static List<NexusUser> LoadUsers()
    {
        return JsonStore.LoadList<NexusUser>(AppPaths.UsersPath);
    }

    public static void SaveScripts(List<ScriptInstance> scripts)
    {
        JsonStore.SaveList(AppPaths.ScriptsPath, scripts);
    }

    public static void SaveQueues(List<DispatchQueue> queues)
    {
        JsonStore.SaveList(AppPaths.QueuesPath, queues);
    }

    public static void SaveUsers(List<NexusUser> users)
    {
        JsonStore.SaveList(AppPaths.UsersPath, users);
    }
}
