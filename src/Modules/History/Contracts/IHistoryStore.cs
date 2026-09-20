using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Modules.History;
namespace NexusPipeline.Modules.History.Contracts;


/// <summary>历史持久化端口，执行域不依赖历史文件实现。</summary>
internal interface IHistoryStore
{
    HistorySaveResult Save(
        RunRecord record,
        List<string> attemptLogs,
        IReadOnlyList<RunScreenshot> screenshots);

    IReadOnlyDictionary<string, int> GetSuccessfulRunsByUser(DateTime date, string scriptInstanceId);

    void Cleanup(int retentionDays);
}
