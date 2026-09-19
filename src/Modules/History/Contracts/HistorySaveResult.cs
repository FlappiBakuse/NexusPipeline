using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Modules.History;
namespace NexusPipeline.Modules.History.Contracts;


/// <summary>历史提交结果：调用方只发布已经确定的不可变快照，并显式感知持久化告警。</summary>
internal sealed record HistorySaveResult(RunRecord Record, string? PersistenceWarning);
