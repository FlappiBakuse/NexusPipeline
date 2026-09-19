using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.Users.Contracts;

namespace NexusPipeline.Host.Composition.Adapters;

internal sealed class UserMutationAdmission : IUserMutationAdmission
{
    private readonly ExecutionDispatcher _dispatcher;

    public UserMutationAdmission(ExecutionDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public void WithCoordination(Action mutation)
    {
        _dispatcher.WithAdmissionCoordination(mutation);
    }
}
