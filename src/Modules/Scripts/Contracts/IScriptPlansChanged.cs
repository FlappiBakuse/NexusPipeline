namespace NexusPipeline.Modules.Scripts.Contracts;

/// <summary>Notification port for invalidating scheduler plans after script writes.</summary>
internal interface IScriptPlansChanged
{
    void RevalidatePendingPlans();
}
