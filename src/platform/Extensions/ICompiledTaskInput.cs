using System.Text.Json;

namespace NexusPipeline.Platform.Extensions;

internal interface ICompiledTaskInput
{
    string Fingerprint { get; }
    bool CanExecute { get; }
    JsonElement View { get; }
    void RequireCurrent();
    string Read(string asset, string pointer);
}
