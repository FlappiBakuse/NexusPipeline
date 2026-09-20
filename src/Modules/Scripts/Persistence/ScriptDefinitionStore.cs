using NexusPipeline.Modules.Scripts;
using NexusPipeline.Platform.Storage;
namespace NexusPipeline.Modules.Scripts.Persistence;


/// <summary>数据持久化仓储：脚本、队列与全局用户的 JSON 读写。运行时数据（config/）集中于此层。</summary>
internal static class ScriptDefinitionStore
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

    public static void SaveScripts(List<ScriptInstance> scripts)
    {
        ScriptStorage.SaveScripts(scripts);
        ScriptStorage.NormalizeInMemoryDeclarations(scripts);
    }
}
