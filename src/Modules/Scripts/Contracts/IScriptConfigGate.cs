namespace NexusPipeline.Modules.Scripts.Contracts;

/// <summary>Configuration-edit lease port used by script mutations.</summary>
internal interface IScriptConfigGate
{
    IDisposable? TryAcquire(string scriptId);
}
