using System.Globalization;
using System.Net;
using System.Text.Json;

namespace NexusPipeline.Host.Control.Cli;

internal sealed record ControlCliRequest(Uri Address, HttpMethod Method, string Path, string? Input,
    string? BearerFile, Guid? OperationId, long BinaryMaximumBytes = 0, bool RequiresBody = true,
    string InputMediaType = "application/json", IReadOnlyDictionary<string, string>? FixedHeaders = null)
{
    internal static ControlCliRequest Parse(string[] args)
    {
        if (args.Length == 0) throw Invalid();
        var command = args[0];
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index += 2)
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal) ||
                !options.TryAdd(args[index], args[index + 1])) throw Invalid();
        var address = Origin(Take(options, "--address"));
        var bearer = Optional(options, "--bearer-file");
        var input = Optional(options, "--input");
        Guid? operation = null;
        long binary = 0; var requiresBody = true; var mediaType = "application/json";
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        string path;
        var method = HttpMethod.Get;
        switch (command)
        {
            case "capabilities": path = "/control/v1/capabilities"; break;
            case "status": path = "/control/v1/status"; break;
            case "doctor": path = "/control/v1/doctor"; break;
            case "support": path = "/control/v1/support"; binary = 65536; break;
            case "maintenance-operation": path = "/control/v1/maintenance/operations/" + Id(Take(options, "--id")); break;
            case "maintenance-feed": case "maintenance-refresh":
                var updateChannel = Take(options, "--channel");
                if (updateChannel is not ("stable" or "prerelease")) throw Invalid();
                path = "/control/v1/maintenance/feed/" + updateChannel;
                if (command == "maintenance-refresh") { path += "/refresh"; method = HttpMethod.Post; requiresBody = false; }
                break;
            case "maintenance-prepare": path = "/control/v1/maintenance/prepare"; method = HttpMethod.Post; break;
            case "host-restart": path = "/control/v1/maintenance/restart/" + Id(Take(options, "--id"));
                method = HttpMethod.Post; requiresBody = false; break;
            case "maintenance-start": case "maintenance-cancel":
                path = "/control/v1/maintenance/operations/" + Id(Take(options, "--id")) + "/" +
                    (command == "maintenance-start" ? "start" : "cancel"); method = HttpMethod.Post; requiresBody = false; break;
            case "operation": path = "/control/v1/operations/" + Id(Take(options, "--id")); break;
            case "configuration-resources":
                path = "/control/v1/configuration/generic/" + Id(Take(options, "--binding-id")); break;
            case "project-context": case "project-discover": case "project-select": case "project-result": case "project-preview": case "project-page": case "project-release":
                path = "/control/v1/projects/" + command["project-".Length..]; method = HttpMethod.Post; break;
            case "configuration-value-preview": case "configuration-value-apply":
                path = "/control/v1/configuration/generic/" + Id(Take(options, "--binding-id")) + "/value/" +
                    (command == "configuration-value-preview" ? "preview" : "apply"); method = HttpMethod.Post; break;
            case "adapter-discover": case "adapter-propose": case "adapter-preview-repair": case "adapter-apply-repair":
                path = "/control/v1/adapters/" + (command switch { "adapter-discover" => "discover", "adapter-propose" => "propose",
                    "adapter-preview-repair" => "previewRepair", _ => "applyRepair" }); method = HttpMethod.Post; break;
            case "resolve": path = Query("/control/v1/automation/resolve", options, ("--kind", "kind"), ("--value", "value")); break;
            case "automation": path = "/control/v1/automation"; break;
            case "emulator-discover": path = "/control/v1/automation/emulators/discover"; method = HttpMethod.Post; break;
            case "script-discover": path = "/control/v1/automation/scripts/discover"; method = HttpMethod.Post; break;
            case "scheduling": path = "/control/v1/scheduling"; break;
            case "automation-change": path = "/control/v1/automation/change"; method = HttpMethod.Post; break;
            case "settings": path = "/control/v1/settings"; break;
            case "settings-save": path = "/control/v1/settings"; method = HttpMethod.Post; break;
            case "extensions": path = "/control/v1/extensions"; break;
            case "extension-catalog": path = "/control/v1/extensions/catalog/" + Channel(Take(options, "--channel")); break;
            case "extension-refresh": path = "/control/v1/extensions/catalog/" + Channel(Take(options, "--channel")) + "/refresh";
                method = HttpMethod.Post; requiresBody = false; break;
            case "extension-operation": path = "/control/v1/extensions/operations/" + Id(Take(options, "--id")); break;
            case "extension-apply": path = "/control/v1/extensions/apply"; method = HttpMethod.Post; break;
            case "extension-reconcile": path = "/control/v1/extensions/reconcile"; method = HttpMethod.Post; break;
            case "automation-import-preview": case "automation-import-apply":
                path = "/control/v1/automation/import/" + (command == "automation-import-preview" ? "preview" : "apply");
                method = HttpMethod.Post; break;
            case "automation-export":
                path = Query("/control/v1/automation/export", options, ("--revision", "revision"), ("--template-script-id", "templateScriptId")); break;
            case "automation-attachment":
                path = Query("/control/v1/automation/export/" + Id(Take(options, "--asset-id")) + "/original", options,
                    ("--revision", "revision"), ("--template-script-id", "templateScriptId"), ("--source-sha256", "sourceSha256"));
                binary = 1073741824; break;
            case "configuration-files":
                path = Query(Configuration(options) + "/files", options, ("--revision", "revision"), ("--after-position", "afterPosition"), ("--limit", "limit")); break;
            case "configuration-original":
                path = Query(Configuration(options) + "/original", options, ("--revision", "revision"), ("--relative-path", "relativePath"),
                    ("--source-sha256", "sourceSha256")); binary = 1073741824; break;
            case "configuration-editor": path = "/control/v1/configuration/editor/" + Id(Take(options, "--session-id")); break;
            case "configuration-editor-begin": path = "/control/v1/configuration/editor/begin"; method = HttpMethod.Post; break;
            case "configuration-import": path = "/control/v1/configuration/import"; method = HttpMethod.Post; break;
            case "configuration-editor-save": case "configuration-editor-cancel":
                path = "/control/v1/configuration/editor/" + Id(Take(options, "--session-id")) + "/" +
                    (command == "configuration-editor-save" ? "save" : "cancel"); method = HttpMethod.Post; break;
            case "media-open": path = "/control/v1/media/previews"; method = HttpMethod.Post; break;
            case "media-frame": path = "/control/v1/media/previews/" + Id(Take(options, "--preview-id")) + "/frame"; binary = 8388608; break;
            case "media-close": path = "/control/v1/media/previews/" + Id(Take(options, "--preview-id")) + "/close";
                method = HttpMethod.Post; requiresBody = false; break;
            case "events-snapshot": path = "/control/v1/events/snapshot"; break;
            case "events": path = Query("/control/v1/events", options, ("--stream-id", "streamId"), ("--after-sequence", "afterSequence"));
                binary = 4194304; break;
            case "completion-revoke": path = "/control/v1/local/completion/revoke"; method = HttpMethod.Post; requiresBody = false; break;
            case "control-token-operation": path = "/control/v1/local/control-token/operations/" + Id(Take(options, "--id")); break;
            case "control-token-cancel": path = "/control/v1/local/control-token/" + Id(Take(options, "--id")) + "/cancel";
                method = HttpMethod.Post; requiresBody = false; break;
            case "secret-status": case "secret-read": case "secret-change": case "control-token-change":
                path = command == "control-token-change" ? "/control/v1/local/control-token/change" :
                    "/control/v1/secrets/" + Segment(Take(options, "--extension-id")) + "/" + Id(Take(options, "--subject-id")) + "/" + Segment(Take(options, "--field"));
                if (command == "secret-read") { path += "/read"; method = HttpMethod.Post; binary = 196608; requiresBody = false; }
                else if (command != "secret-status")
                {
                    if (command == "secret-change") path += "/change";
                    method = HttpMethod.Post; operation = Id(Take(options, "--id"));
                    var disposition = Take(options, "--disposition");
                    if (disposition is not ("keep" or "set" or "clear") || command == "control-token-change" && disposition == "keep") throw Invalid();
                    headers.Add("Nexus-Operation-Id", operation.Value.ToString("D"));
                    var revision = Take(options, "--expected-revision");
                    if (!long.TryParse(revision, NumberStyles.None, CultureInfo.InvariantCulture, out var expected) ||
                        expected is < 0 or > 9007199254740991 || expected.ToString(CultureInfo.InvariantCulture) != revision) throw Invalid();
                    headers.Add("Nexus-Expected-Revision", revision);
                    headers.Add("Nexus-Secret-Disposition", disposition);
                    requiresBody = disposition == "set"; mediaType = "text/plain";
                }
                break;
            case "notifications": path = "/control/v1/notifications"; break;
            case "notification-credentials": path = "/control/v1/notifications/" + Id(Take(options, "--task-id")) + "/credentials"; break;
            case "service-settings": path = "/control/v1/settings/service"; break;
            case "service-settings-save": path = "/control/v1/settings/service"; method = HttpMethod.Post; break;
            case "notification-save": case "notification-delete": case "notification-reorder":
            case "notification-limit": case "notification-test":
                path = "/control/v1/notifications/" + command["notification-".Length..];
                method = HttpMethod.Post; break;
            case "execution-preview": path = "/control/v1/execution/preview"; method = HttpMethod.Post; break;
            case "execution-start":
                operation = Id(Take(options, "--id"));
                path = "/control/v1/execution/start/" + operation.Value.ToString("D"); method = HttpMethod.Post; break;
            case "execution-cancel":
                operation = Id(Take(options, "--id"));
                path = "/control/v1/execution/" + Id(Take(options, "--start-operation-id")) +
                    "/cancel/" + operation.Value.ToString("D"); method = HttpMethod.Post; break;
            case "records":
                path = Query("/control/v1/records", options, ("--limit", "limit"), ("--automation-id", "automationId"),
                    ("--user-id", "userId"), ("--business", "business"), ("--after-ended-at", "afterEndedAt"),
                    ("--after-run-id", "afterRunId"), ("--ended-from-inclusive", "endedFromInclusive"),
                    ("--ended-until-exclusive", "endedUntilExclusive")); break;
            case "record-attempts":
                path = Query("/control/v1/records/" + Id(Take(options, "--run-id")) + "/attempts", options,
                    ("--limit", "limit"), ("--after-ordinal", "afterOrdinal")); break;
            case "record-logs": case "record-images":
                path = Query(Attempt(options) + (command == "record-logs" ? "/logs" : "/images"), options,
                    ("--limit", "limit"), ("--after-position", "afterPosition")); break;
            case "record-retire":
                path = "/control/v1/records/" + Id(Take(options, "--run-id")) + "/retire/" + RetireKind(Take(options, "--kind"));
                method = HttpMethod.Post; break;
            case "record-image-original":
                path = Query(Attempt(options) + "/images/" + Segment(Take(options, "--step-id")) + "/" + Id(Take(options, "--capture-id")) + "/original",
                    options, ("--result-sha256", "resultSha256")); binary = 8388608; break;
            case "record-log-description":
                path = Log(options) + "/description"; break;
            case "record-log-original":
                path = Query(Log(options) + "/original", options, ("--snapshot-sha256", "snapshotSha256")); binary = 67108864; break;
            case "record-log-part":
                path = Query(Log(options) + "/parts/" + Number(Take(options, "--part-index")), options,
                    ("--snapshot-sha256", "snapshotSha256")); binary = 65536; break;
            case "record-log-tail":
                path = Query(Attempt(options) + "/logs/" + Segment(Take(options, "--step-id")) + "/" +
                    Segment(Take(options, "--role")) + "/tail", options, ("--maximum-bytes", "maximumBytes")); break;
            default: throw Invalid();
        }
        if (binary != 0 && Take(options, "--binary-stdout") != "true") throw Invalid();
        if (options.Count != 0 || method == HttpMethod.Post && requiresBody != (input is not null) ||
            method == HttpMethod.Get && input is not null) throw Invalid();
        return new(address, method, path, input, bearer, operation, binary, requiresBody, mediaType, headers);
    }

    private static string Configuration(Dictionary<string, string> options) =>
        "/control/v1/configuration/generic/" + Id(Take(options, "--binding-id")) + "/" + Segment(Take(options, "--resource-id"));
    private static string Log(Dictionary<string, string> options) =>
        Attempt(options) + "/logs/" + Segment(Take(options, "--step-id")) + "/" + Segment(Take(options, "--role"));
    private static string Channel(string value) => value is "stable" or "develop" ? value : throw Invalid();
    private static string RetireKind(string value) => value is "logs" or "images" ? value : throw Invalid();
    private static string Number(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) &&
        number >= 0 && number.ToString(CultureInfo.InvariantCulture) == value ? value : throw Invalid();
    private static string Attempt(Dictionary<string, string> options) =>
        "/control/v1/records/" + Id(Take(options, "--run-id")) + "/attempts/" + Id(Take(options, "--attempt-id"));
    private static string Query(string path, Dictionary<string, string> options, params (string Option, string Query)[] fields)
    {
        var values = new List<string>();
        foreach (var (option, query) in fields)
            if (Optional(options, option) is { } value)
            {
                if (value.Length is 0 or > 1024 || value.Any(char.IsControl)) throw Invalid();
                values.Add(query + "=" + Uri.EscapeDataString(value));
            }
        return path + (values.Count == 0 ? "" : "?" + string.Join("&", values));
    }
    private static Uri Origin(string text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.Port is < 1 or > 65535 || !IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out _) && uri.IdnHost != "localhost")
            throw Invalid();
        return uri;
    }
    private static Guid Id(string text) => Guid.TryParseExact(text, "D", out var id) && id != Guid.Empty && id.ToString("D") == text
        ? id : throw Invalid();
    private static string Segment(string text) => text.Length is > 0 and <= 128 && !text.Any(char.IsControl)
        ? Uri.EscapeDataString(text) : throw Invalid();
    private static string Take(Dictionary<string, string> options, string name) => Optional(options, name) ?? throw Invalid();
    private static string? Optional(Dictionary<string, string> options, string name) => options.Remove(name, out var value) ? value : null;
    private static ArgumentException Invalid() => new("control_cli_arguments_invalid");
}
