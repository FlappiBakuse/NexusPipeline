using NexusPipeline.Extensibility;

namespace NexusPipeline.Plugins;

internal sealed partial class DataSpecializedPlugin
{
    public ScriptProfile? Resolve(string rootPath, IReadOnlyDictionary<string, string>? inputs) =>
        _profileResolver.Resolve(rootPath, inputs);
}
