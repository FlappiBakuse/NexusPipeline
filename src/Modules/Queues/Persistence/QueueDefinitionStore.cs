using NexusPipeline.Modules.Queues;
using NexusPipeline.Platform.Storage;
namespace NexusPipeline.Modules.Queues.Persistence;


/// <summary>数据持久化仓储：脚本、队列与全局用户的 JSON 读写。运行时数据（config/）集中于此层。</summary>
internal static class QueueDefinitionStore
{

    public static List<DispatchQueue> LoadQueues()
    {
        return JsonStore.LoadList<DispatchQueue>(AppPaths.QueuesPath);
    }

    public static void SaveQueues(List<DispatchQueue> queues)
    {
        JsonStore.SaveList(AppPaths.QueuesPath, queues);
    }
}
