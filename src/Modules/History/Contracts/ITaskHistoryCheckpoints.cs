namespace NexusPipeline.Modules.History.Contracts;

internal interface ITaskHistoryCheckpoints
{
    void SaveTaskCheckpoint(RunRecord record);
}
