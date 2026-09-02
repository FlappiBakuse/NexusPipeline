using NexusPipeline.Models;
namespace NexusPipeline.Persistence;

/// <summary>数据持久化仓储：脚本、队列与全局用户的 JSON 读写。运行时数据（config/）集中于此层。</summary>
internal static class DataStore
{
    private static readonly ScriptStorage ScriptStorage = new(AppPaths.AppRoot);

    public static List<ScriptInstance> LoadScripts()
    {
        return ScriptStorage.LoadScripts();
    }

    public static List<ScriptInstance> LoadScripts(out bool authoritative)
    {
        List<ScriptInstance> scripts = ScriptStorage.LoadScripts();
        authoritative = ScriptStorage.LastLoadAuthoritative;
        return scripts;
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
        ScriptStorage.SaveScripts(scripts);
        ScriptStorage.NormalizeInMemoryDeclarations(scripts);
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
