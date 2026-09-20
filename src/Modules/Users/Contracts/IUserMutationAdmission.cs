namespace NexusPipeline.Modules.Users.Contracts;

/// <summary>Host admission/coordination port for user mutations.</summary>
internal interface IUserMutationAdmission
{
    void WithCoordination(Action mutation);
}
