namespace NexusPipeline.Modules.Users.Contracts;

/// <summary>Daily RunDays persistence port consumed by Scheduling.</summary>
internal interface IUserRunDaysMaintenance
{
    bool DecrementDaily();
}
