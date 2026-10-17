using System.Text.Json;
using System.Security.Cryptography;
using NexusPipeline.Modules.Plugins.Runtime;
using NexusPipeline.Modules.Plugins.Managed;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Shared.Logging;
using NexusPipeline.Shared.Serialization;

namespace NexusPipeline.Modules.Plugins.Repository;

internal static partial class PluginInstallRecovery
{
    private static readonly string[] PendingStateProperties = [nameof(PluginPendingState.SchemaVersion), nameof(PluginPendingState.Operations)];

    private static readonly string[] PendingOperationRequiredProperties =
    [
        nameof(PluginPendingOperation.Action),
        nameof(PluginPendingOperation.Name),
        nameof(PluginPendingOperation.ArtifactName),
        nameof(PluginPendingOperation.Version),
        nameof(PluginPendingOperation.Kind),
        nameof(PluginPendingOperation.ApiVersion),
        nameof(PluginPendingOperation.Sha256),
        nameof(PluginPendingOperation.StagedPath),
        nameof(PluginPendingOperation.BackupPath),
        nameof(PluginPendingOperation.Phase),
        nameof(PluginPendingOperation.CreatedAt),
        nameof(PluginPendingOperation.Channel),
        nameof(PluginPendingOperation.SourceCommit),
        nameof(PluginPendingOperation.OperationId),
        nameof(PluginPendingOperation.EnableAfterInstall),
    ];

    private static readonly string[] OwnershipStateProperties = [nameof(PluginOwnershipState.SchemaVersion), nameof(PluginOwnershipState.Plugins)];

    private static readonly string[] OwnershipRequiredProperties =
    [
        nameof(PluginOwnership.Name),
        nameof(PluginOwnership.ArtifactName),
        nameof(PluginOwnership.Version),
        nameof(PluginOwnership.Kind),
        nameof(PluginOwnership.ApiVersion),
        nameof(PluginOwnership.Sha256),
        nameof(PluginOwnership.InstalledAt),
        nameof(PluginOwnership.Channel),
        nameof(PluginOwnership.SourceCommit),
    ];

    public static IReadOnlyList<PluginPendingOperation> ReadPending(string? path = null)
    {
        lock (Sync)
        {
            return LoadPending(path ?? AppPaths.PluginPendingPath).Operations
                .Select(Clone)
                .ToArray();
        }
    }

    public static IReadOnlyDictionary<string, PluginOwnership> ReadOwnership(string? path = null)
    {
        lock (Sync)
        {
            try
            {
                return LoadOwnership(path ?? AppPaths.PluginOwnershipPath).Plugins
                    .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException)
            {
                // 归属记录是安全边界而不是启动必需配置。复制旧客户端/损坏文件时，
                // 只放弃商店接管能力并保留原文件，不能让状态页因异常而永久停在连接中。
                Logger.Warn($"[插件] ownership.json 无法按当前格式读取，已按未托管处理：{ex.Message}");
                return new Dictionary<string, PluginOwnership>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public static void AddPending(PluginPendingOperation operation, string? path = null)
    {
        if (!PluginRepositoryCatalog.IsCanonicalPluginId(operation.Name))
        {
            throw new InvalidDataException($"插件名称不安全：{operation.Name}");
        }
        if (!PluginRepositoryCatalog.IsSafeArtifactName(operation.ArtifactName))
        {
            throw new InvalidDataException($"插件物理目录名不安全：{operation.ArtifactName}");
        }
        lock (Sync)
        {
            string file = path ?? AppPaths.PluginPendingPath;
            PluginPendingState state = LoadPending(file);
            if (state.Operations.Any(item => string.Equals(item.Name, operation.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"插件已有待处理事务：{operation.Name}");
            }
            state.Operations.Add(Clone(operation));
            SavePending(file, state);
        }
    }

    private static void UpsertOwnership(PluginOwnershipState state, PluginOwnership item)
    {
        state.Plugins.RemoveAll(existing => string.Equals(existing.Name, item.Name, StringComparison.OrdinalIgnoreCase));
        state.Plugins.Add(item);
    }

    private static PluginPendingState LoadPending(string path)
    {
        if (!File.Exists(path))
        {
            return new PluginPendingState();
        }
        string text = File.ReadAllText(path);
        ValidatePendingDocument(text);
        PluginPendingState state = JsonSerializer.Deserialize<PluginPendingState>(text, JsonOpts.Default)
            ?? throw new InvalidDataException("插件 pending.json 为空");
        if (state.SchemaVersion != CurrentStateSchema)
        {
            throw new InvalidDataException($"不支持的插件 pending schemaVersion：{state.SchemaVersion}");
        }
        if (state.Operations is null)
        {
            throw new InvalidDataException("插件 pending.json 缺少 operations");
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PluginPendingOperation operation in state.Operations)
        {
            ValidatePendingOperation(operation);
            if (!names.Add(operation.Name))
            {
                throw new InvalidDataException($"插件 pending 存在重复归属：{operation.Name}");
            }
        }
        return state;
    }

    private static PluginOwnershipState LoadOwnership(string path)
    {
        if (!File.Exists(path))
        {
            return new PluginOwnershipState();
        }
        string text = File.ReadAllText(path);
        ValidateOwnershipDocument(text);
        PluginOwnershipState state = JsonSerializer.Deserialize<PluginOwnershipState>(text, JsonOpts.Default)
            ?? throw new InvalidDataException("插件 ownership.json 为空");
        if (state.SchemaVersion != CurrentStateSchema)
        {
            throw new InvalidDataException($"不支持的插件 ownership schemaVersion：{state.SchemaVersion}");
        }
        if (state.Plugins is null)
        {
            throw new InvalidDataException("插件 ownership.json 缺少 plugins");
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PluginOwnership owner in state.Plugins)
        {
            if (!PluginRepositoryCatalog.IsCanonicalPluginId(owner.Name)
                || !PluginRepositoryCatalog.IsSafeArtifactName(owner.ArtifactName)
                || !names.Add(owner.Name))
            {
                throw new InvalidDataException($"插件 ownership 归属无效或重复：{owner.Name}");
            }
            ValidateProvenance(owner.Channel, owner.SourceCommit, owner.Name);
        }
        return state;
    }

    private static void ValidateDocumentShape(
        string json,
        string label,
        IReadOnlyCollection<string> required,
        IReadOnlyCollection<string> allowed)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{label}根节点必须是对象");
        }
        var properties = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
            if (!properties.Add(property.Name))
                throw new InvalidDataException($"{label}包含重复字段");
        if (required.Any(property => !properties.Contains(property))
            || properties.Any(property => !allowed.Contains(property)))
        {
            throw new InvalidDataException($"{label}不是当前格式");
        }
    }

    private static void ValidatePendingDocument(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        ValidateDocumentShape(json, "插件 pending.json", PendingStateProperties, PendingStateProperties);
        JsonElement operations = document.RootElement.GetProperty(nameof(PluginPendingState.Operations));
        if (operations.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("插件 pending.json 的 operations 必须是数组");
        }
        foreach (JsonElement operation in operations.EnumerateArray())
        {
            ValidateDocumentShape(
                operation.GetRawText(),
                "插件 pending 操作",
                PendingOperationRequiredProperties,
                PendingOperationRequiredProperties);
        }
    }

    private static void ValidateOwnershipDocument(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        ValidateDocumentShape(json, "插件 ownership.json", OwnershipStateProperties, OwnershipStateProperties);
        JsonElement plugins = document.RootElement.GetProperty(nameof(PluginOwnershipState.Plugins));
        if (plugins.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("插件 ownership.json 的 plugins 必须是数组");
        }
        foreach (JsonElement owner in plugins.EnumerateArray())
        {
            ValidateDocumentShape(
                owner.GetRawText(),
                "插件 ownership 记录",
                OwnershipRequiredProperties,
                OwnershipRequiredProperties);
        }
    }

    private static void ValidatePendingOperation(PluginPendingOperation operation)
    {
        if (!PluginRepositoryCatalog.IsCanonicalPluginId(operation.Name)
            || !PluginRepositoryCatalog.IsSafeArtifactName(operation.ArtifactName)
            || operation.Action is not ("install" or "update" or "uninstall")
            || operation.Phase is not ("pending" or "backed-up" or "swapped")
            || string.IsNullOrWhiteSpace(operation.StagedPath)
            || !Path.IsPathRooted(operation.StagedPath)
            || (!string.IsNullOrWhiteSpace(operation.BackupPath) && !Path.IsPathRooted(operation.BackupPath)))
        {
            throw new InvalidDataException($"插件 pending 操作字段无效：{operation.Name}");
        }
        if ((!string.IsNullOrEmpty(operation.OperationId)
                && (!Guid.TryParseExact(operation.OperationId, "N", out _)
                    || operation.Action != "install"))
            || operation.EnableAfterInstall
                && (operation.Action != "install" || operation.Kind != "managed-code"
                    || string.IsNullOrEmpty(operation.OperationId)))
        {
            throw new InvalidDataException($"插件 pending 启用意图无效：{operation.Name}");
        }
        ValidateProvenance(operation.Channel, operation.SourceCommit, operation.Name);
    }

    private static void ValidateProvenance(string channel, string sourceCommit, string name)
    {
        if (sourceCommit is null) throw new InvalidDataException($"插件事务来源元数据无效：{name}");
        bool validCommit = sourceCommit.Length == 40
            && sourceCommit.All(ch => char.IsAsciiDigit(ch) || ch is >= 'a' and <= 'f');
        if (channel is not ("stable" or "develop")
            || channel == "develop" && !validCommit
            || channel == "stable" && sourceCommit.Length > 0 && !validCommit)
        {
            throw new InvalidDataException($"插件事务来源元数据无效：{name}");
        }
    }

    private static PluginOwnership? FindOwnership(string name, PluginOwnershipState ownership)
    {
        return ownership.Plugins.SingleOrDefault(item =>
            string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static void EnsureOwnedIdentity(
        PluginPendingOperation operation,
        PluginOwnershipState ownership,
        bool requireCurrentVersion)
    {
        PluginOwnership? owner = FindOwnership(operation.Name, ownership);
        if (owner is null
            || !string.Equals(owner.ArtifactName, operation.ArtifactName, StringComparison.Ordinal)
            || requireCurrentVersion && !string.Equals(owner.Version, operation.Version, StringComparison.Ordinal))
        {
            throw new IOException($"插件事务缺少匹配的已验证安装归属，拒绝操作：{operation.Name}/{operation.ArtifactName}");
        }
    }

    private static void SavePending(string path, PluginPendingState state)
    {
        state.SchemaVersion = CurrentStateSchema;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        JsonUtil.WriteAtomic(path, JsonSerializer.Serialize(state, JsonOpts.Indented));
    }

    private static void SaveOwnership(string path, PluginOwnershipState state)
    {
        state.SchemaVersion = CurrentStateSchema;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        JsonUtil.WriteAtomic(path, JsonSerializer.Serialize(state, JsonOpts.Indented));
    }

    private static PluginPendingOperation Clone(PluginPendingOperation source)
    {
        return new PluginPendingOperation
        {
            Action = source.Action,
            Name = source.Name,
            ArtifactName = source.ArtifactName,
            Version = source.Version,
            Kind = source.Kind,
            ApiVersion = source.ApiVersion,
            Sha256 = source.Sha256,
            Channel = source.Channel,
            SourceCommit = source.SourceCommit,
            StagedPath = source.StagedPath,
            BackupPath = source.BackupPath,
            Phase = source.Phase,
            CreatedAt = source.CreatedAt,
            OperationId = source.OperationId,
            EnableAfterInstall = source.EnableAfterInstall,
        };
    }
}
