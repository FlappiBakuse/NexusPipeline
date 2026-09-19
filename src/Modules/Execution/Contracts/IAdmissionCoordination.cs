using NexusPipeline.Plugin.Abstractions;
namespace NexusPipeline.Modules.Execution.Contracts;


/// <summary>宿主准入协调锁端口，供调度器与维护策略共享同一竞态边界。</summary>
internal interface IAdmissionCoordination
{
    T WithAdmissionCoordination<T>(Func<T> action);
}
