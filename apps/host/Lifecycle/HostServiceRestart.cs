using System.Net;
using NexusPipeline.Host.Composition;

namespace NexusPipeline.Host.Lifecycle;

internal sealed record HostRestartReply(Guid OperationId, Guid InstanceId, int ProcessId, int Port, string State);

internal sealed record HostRestartTarget(HostRestartReply Reply, string Origin, ControlListener.RestartReservation Listener)
{
    internal string Path => "/control/v1/maintenance/restart/" + Reply.OperationId.ToString("D");
    internal string Address => new UriBuilder(Origin) { Port = Reply.Port }.Uri.GetLeftPart(UriPartial.Authority);

    internal bool AllowsObserver(HttpContext context) =>
        HttpMethods.IsGet(context.Request.Method) && context.Request.Path.Value == Path &&
        context.Connection.RemoteIpAddress is { } peer &&
        IPAddress.IsLoopback(peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer) &&
        string.Equals(context.Request.Headers.Origin.ToString(), Origin, StringComparison.Ordinal) &&
        string.Equals(context.Request.Scheme + "://" + context.Request.Host.Value, Address, StringComparison.Ordinal);
}

internal sealed class HostServiceRestart(HostRestartTarget? previous)
{
    private int _ready;
    internal HostRestartTarget? Previous { get; } = previous;
    internal HostRestartTarget? Next { get; set; }
    internal void Ready() => Volatile.Write(ref _ready, 1);

    internal HostRestartReply? Read(Guid operationId) =>
        Volatile.Read(ref _ready) == 1 && Previous is { } target && target.Reply.OperationId == operationId
            ? target.Reply with { State = "ready" } : null;
}
