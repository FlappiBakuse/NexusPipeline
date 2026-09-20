namespace NexusPipeline.Modules.Users.Contracts;

/// <summary>Notification port for user definition changes.</summary>
internal interface IUserPlansChanged
{
    void RevalidatePendingPlans();
}
