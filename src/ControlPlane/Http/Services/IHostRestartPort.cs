using NexusPipeline.Modules.Updates;

namespace NexusPipeline.ControlPlane.Http.Services;

/// <summary>Control-plane port for requesting a host restart; the implementation is supplied by Host composition.</summary>
internal interface IHostRestartPort
{
    RestartRequestResult Request(string auditSource);
}
