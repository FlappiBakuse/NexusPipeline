using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Control;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Serialization.Protocol;
using NexusPipeline.Platform.Storage.Extensions;

namespace NexusPipeline.Host.Control.Configuration;

[SupportedOSPlatform("windows")]
internal sealed class SourceUseCases(ScriptSourceService sources, ContractReader contracts)
{
    internal async ValueTask<object> DiscoverAsync(AuthenticatedControlRequest request, JsonElement body, CancellationToken cancellation)
    {
        void Current()
        {
            request.Require(ControlCapability.ConfigurationRead);
        }
        Current();
        var command = contracts.Read<ControlScriptSource>(JsonSerializer.SerializeToUtf8Bytes(body), "control-script-source.schema.json");
        try
        {
            var result = await sources.DiscoverAsync(Guid.Parse(command.OperationId), command.ExtensionId,
                command.ExpectedInstalledRevision, command.RootPath, Current, cancellation);
            Current();
            return new { rootPath = command.RootPath, installedRevision = command.ExpectedInstalledRevision, discovery = result };
        }
        catch (IOException error) when (error.Message.StartsWith("script_source_", StringComparison.Ordinal) ||
            error.Message.StartsWith("project_", StringComparison.Ordinal) || error.Message.StartsWith("extension_", StringComparison.Ordinal))
        { throw new ControlRequestException(409, error.Message); }
    }
}
