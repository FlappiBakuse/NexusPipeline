using System.Diagnostics;
using System.Security.Cryptography;
using NexusPipeline.Modules.Configuration.Contracts;
using NexusPipeline.Modules.Configuration.Exchange;
using NexusPipeline.Modules.Configuration.Paths;
using NexusPipeline.Modules.Configuration.Recovery;
using NexusPipeline.Modules.Configuration.Scripting;
using NexusPipeline.Modules.Configuration.Validation;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Plugins.DataSpecialized;
using NexusPipeline.Modules.Scripts.Contracts;
using NexusPipeline.Modules.Scripts.Resolution;
using NexusPipeline.Modules.Scripts.Validation;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Users.Contracts;
using NexusPipeline.Modules.Users.UseCases;
using NexusPipeline.Modules.Users;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Windows;
using NexusPipeline.Shared.Logging;
using NexusPipeline.Shared.Results;
using NexusPipeline.Modules.Configuration.Snapshots;
using NexusPipeline.Modules.Settings.Contracts;
using NexusPipeline.Modules.Settings;

namespace NexusPipeline.Modules.Configuration.Editing;

internal sealed partial class ConfigEditCommands
{
    private sealed record RepairCandidate(ConfigEditTarget Target, ConfigStoreMetadata Metadata,
        string StoreRoot, string StoreFile, string? ExtraSite, byte[] Before, byte[] After, ConfigRepairProposal Proposal);

    public OperationResult<ConfigRepairProposal> PreviewRepair(string scriptId, string userId)
    {
        OperationResult<ConfigRepairProposal> Unavailable(string reason) => OperationResult<ConfigRepairProposal>.Ok(
            new(false, reason, "", userId, scriptId, "", null, null, "", null));
        if (_settingsProvider?.Current.AllowConfigRepair != true)
            return Unavailable("config_repair_disabled");
        try
        {
            OperationResult<RepairCandidate> candidate = ReadRepairCandidate(scriptId, userId);
            if (candidate.Error?.Code is "config_repair_unavailable" or "config_repair_none")
                return Unavailable(candidate.Error.Code);
            return candidate.Succeeded
                ? OperationResult<ConfigRepairProposal>.Ok(candidate.Value!.Proposal)
                : OperationResult<ConfigRepairProposal>.Failure(candidate.Error!);
        }
        catch (Exception ex) { return Internal<ConfigRepairProposal>(ex); }
    }

    public OperationResult<ConfigRepairProposal> ApplyRepair(string scriptId, string userId, string token,
        string source = Audit.Web)
    {
        if (_settingsProvider?.Current.AllowConfigRepair != true)
            return Validation<ConfigRepairProposal>("配置修复开关已关闭", "config_repair_disabled");
        if (string.IsNullOrWhiteSpace(token) || token.Length != 64 || !token.All(Uri.IsHexDigit))
            return Validation<ConfigRepairProposal>("配置修复预览令牌无效", "config_repair_stale");
        OperationResult<ConfigEditTarget> initial = ResolveTarget(scriptId, userId);
        if (!initial.Succeeded) return OperationResult<ConfigRepairProposal>.Failure(initial.Error!);
        ConfigEditTarget target = initial.Value!;
        ScriptConfigGate.Lease? gate = ScriptConfigGate.Get(target.Script.Id);
        bool gateHeld = false, editLeaseHeld = false;
        bool gateBusy = false;
        string? editConflict = null;
        string? scratch = null;
        bool repairCommitted = false;
        try
        {
            bool changed = _admission.TryExecuteLeaseMutation(target.Script.Id, target.UserKey, () =>
            {
                if (!gate!.Wait(0)) { gateBusy = true; return; }
                gateHeld = true;
                if (_admission.TryBeginEditSession(target.Script.Id, target.UserKey,
                    target.Script.ConfigPath, out editConflict)) editLeaseHeld = true;
            }, out IReadOnlyList<ExecutionLeaseReference> leases, out string? failureCode);
            if (!changed) return LeaseConflict<ConfigRepairProposal>(leases,
                $"user:{target.Script.Id}:{target.UserKey}", failureCode);
            if (gateBusy || !gateHeld || !editLeaseHeld)
                return Conflict<ConfigRepairProposal>("resource_busy", editConflict ?? "脚本正在运行或编辑配置中");
            if (_settingsProvider?.Current.AllowConfigRepair != true)
                return Validation<ConfigRepairProposal>("配置修复开关已关闭", "config_repair_disabled");
            OperationResult<RepairCandidate> current = ReadRepairCandidate(scriptId, userId);
            if (!current.Succeeded) return OperationResult<ConfigRepairProposal>.Failure(current.Error!);
            RepairCandidate candidate = current.Value!;
            byte[] supplied = Convert.FromHexString(token);
            byte[] actual = Convert.FromHexString(candidate.Proposal.Token!);
            if (!CryptographicOperations.FixedTimeEquals(supplied, actual))
                return Conflict<ConfigRepairProposal>("config_repair_stale", "配置修复预览已过期，请重新预览");
            if (SystemActions.IsExeRunning(candidate.Target.Script.MainExe)
                || SystemActions.IsExeRunning(ResolveLaunchTargetExe(candidate.Target.Script)))
                return Conflict<ConfigRepairProposal>("resource_busy", "脚本程序正在运行，请退出后重试");
            // Source is a private work copy. The existing store transaction owns the only durable mutation.
            scratch = Path.Combine(ConfigPaths.UserDir(scriptId, candidate.Target.UserKey),
                "repair-backups", Guid.NewGuid().ToString("N"), "work");
            Directory.CreateDirectory(scratch);
            string workCopy = Path.Combine(scratch, "snapshot");
            CopyRepairSnapshot(candidate.StoreRoot, workCopy);
            string sourceFile = Path.Combine(workCopy, Path.GetRelativePath(candidate.StoreRoot, candidate.StoreFile));
            File.WriteAllBytes(sourceFile, candidate.After);
            var mark = new ConfigSessionMark
            {
                ScriptId = scriptId, UserId = candidate.Target.UserKey,
                ConfigPath = candidate.Target.Script.ConfigPath, ConfigKind = candidate.Metadata.ConfigKind,
                PluginName = candidate.Metadata.PluginName, PluginVersion = candidate.Metadata.PluginVersion,
                ProfileHash = candidate.Metadata.ProfileHash, ConfigContractId = candidate.Metadata.ConfigContractId,
            };
            void ValidateCurrent()
            {
                if (_settingsProvider?.Current.AllowConfigRepair != true) throw new ConfigRepairDisabledException();
                var latest = ReadRepairCandidate(scriptId, userId, duringCommit: true);
                if (!latest.Succeeded || latest.Value!.Proposal.Token != token)
                    throw new IOException("配置修复预览已过期，原快照保持不变");
            }
            void Commit()
            {
                ValidateCurrent();
                string backup = Path.GetDirectoryName(scratch)!;
                Directory.CreateDirectory(backup);
                File.WriteAllBytes(Path.Combine(backup, "original"), candidate.Before);
                File.Copy(ConfigPaths.StoreMetadataPath(scriptId, candidate.Target.UserKey), Path.Combine(backup, "store-meta.json"));
                File.WriteAllText(Path.Combine(backup, "repair.json"), System.Text.Json.JsonSerializer.Serialize(new
                {
                    scriptId, userId = candidate.Target.UserKey, resource = candidate.Proposal.Field,
                    storeFile = candidate.StoreFile, plugin = candidate.Target.Script.PluginType,
                    pluginVersion = candidate.Target.Spec!.PluginVersion,
                    beforeSha256 = Convert.ToHexString(SHA256.HashData(candidate.Before)),
                    afterSha256 = Convert.ToHexString(SHA256.HashData(candidate.After)),
                }));
                if (candidate.ExtraSite is not null)
                {
                    var diff = ConfigStoreDiff.Build(workCopy, candidate.StoreRoot, new HashSet<string>(), null);
                    ExtraConfigStoreTransaction.Apply(scriptId, candidate.Target.UserKey, candidate.ExtraSite,
                        candidate.StoreRoot, diff, ConfigSwapSession.SampleConfig(workCopy), sourcePath: workCopy,
                        validateCurrent: ValidateCurrent);
                }
                else
                    ConfigStoreTransaction.Apply(scriptId, candidate.Target.UserKey,
                        candidate.Metadata.ConfigKind == "file" ? sourceFile : workCopy,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase), null, null, mark,
                        preserveExistingMetadata: true, expectedStoreFile: candidate.StoreFile,
                        expectedStoreBytes: candidate.Before, validateCurrent: ValidateCurrent);
            }
            if (_settingsProvider is SettingsState state)
            {
                lock (state.MutationLock) Commit();
            }
            else Commit();
            Audit.Log(source, "应用配置修复", $"{candidate.Target.Script.Name} / {candidate.Target.User.UserName}：{candidate.Proposal.Field}");
            repairCommitted = true;
            return OperationResult<ConfigRepairProposal>.Ok(candidate.Proposal with { Token = null });
        }
        catch (ConfigRepairDisabledException) { return Validation<ConfigRepairProposal>("配置修复开关已关闭", "config_repair_disabled"); }
        catch (InvalidDataException ex) { return Conflict<ConfigRepairProposal>("config_repair_stale", ex.Message); }
        catch (IOException ex) { return Conflict<ConfigRepairProposal>("config_repair_stale", ex.Message); }
        catch (Exception ex) { return Internal<ConfigRepairProposal>(ex); }
        finally
        {
            if (repairCommitted && scratch is not null)
            {
                try
                {
                    Directory.Delete(scratch, true);
                }
                catch (IOException) { /* Retain owned scratch if cleanup cannot finish. */ }
            }
            if (editLeaseHeld) _admission.EndEditSession(target.Script.Id, target.UserKey);
            if (gateHeld) gate!.Release();
            gate?.Dispose();
        }
    }

    private OperationResult<RepairCandidate> ReadRepairCandidate(string scriptId, string userId, bool duringCommit = false)
    {
        OperationResult<ConfigEditTarget> resolved = ResolveTarget(scriptId, userId);
        if (!resolved.Succeeded) return OperationResult<RepairCandidate>.Failure(resolved.Error!);
        ConfigEditTarget target = resolved.Value!;
        if (target.Spec?.TaskProtocol?.RepairRules is not { Length: > 0 } rules || target.Script.RequiresReconfiguration
            || PluginAvailability.GetUnavailableReason(target.Script, _pluginAvailability) is not null)
            return Validation<RepairCandidate>("当前插件未声明可用修复", "config_repair_unavailable");
        string store = ConfigPaths.StoreDir(scriptId, target.UserKey);
        if (!Directory.Exists(store) || !duringCommit && Directory.Exists(ConfigPaths.StoreTransactionDir(scriptId, target.UserKey))
            || File.Exists(ConfigPaths.StoreTransactionBlockedPath(scriptId, target.UserKey))
            || !duringCommit && ExtraConfigStoreTransaction.HasAnyResidue(scriptId, target.UserKey)
            || ConfigSessionMark.TryRead(scriptId, target.UserKey) is not null)
            return Validation<RepairCandidate>("用户快照不可用或存在未完成事务", "config_repair_unavailable");
        ConfigStoreMetadata? metadata = ConfigStoreMetadata.Load(scriptId, target.UserKey);
        if (metadata is null || metadata.ConfigKind is not ("file" or "dir")
            || metadata.PluginName != target.Script.PluginType || metadata.ConfigContractId != target.Spec.ConfigContractId
            || metadata.ConfigLocatorHash != ConfigStoreMetadata.HashLocator(target.Script.ConfigPath))
            return Validation<RepairCandidate>("配置快照归属已变化", "config_repair_unavailable");
        string fingerprint = ConfigSnapshotFingerprint.Fingerprint(store)
            + ConfigSnapshotFingerprint.Fingerprint(ConfigPaths.StoreMetadataPath(scriptId, target.UserKey));
        foreach (string extra in target.Spec.ExtraConfigPaths)
            fingerprint += ConfigSnapshotFingerprint.Fingerprint(ConfigPaths.StoreExtraDir(scriptId, target.UserKey, extra));
        var context = new ConfigRepairContext(target.Script.GameExe, target.Script.GameArgs,
            !string.Equals(target.Script.GameMode, "emulator", StringComparison.OrdinalIgnoreCase), target.Spec.ConfigInputValue);
        string identity = target.Spec.ProfileHash + fingerprint + System.Text.Json.JsonSerializer.Serialize(context) + target.Spec.ConfigEditor?.Script;
        foreach (var rule in rules)
        {
            if (rule.SnapshotKind == "file" && metadata.ConfigKind != "file"
                || rule.NoExtraConfig && target.Spec.ExtraConfigPaths.Count != 0) continue;
            string root = store, relative = rule.ResourceId, extraSite = "";
            if (relative == "extra:0")
            {
                if (target.Spec.ExtraConfigPaths.Count == 0) continue;
                extraSite = target.Spec.ExtraConfigPaths[0];
                root = ConfigPaths.StoreExtraDir(scriptId, target.UserKey, extraSite);
                relative = Path.GetFileName(extraSite);
            }
            else if (relative == "config:$main")
            {
                if (metadata.ConfigKind != "file") continue;
                relative = Path.GetFileName(target.Script.ConfigPath);
            }
            else
            {
                if (!relative.StartsWith("config:", StringComparison.Ordinal)) continue;
                relative = relative[7..];
                if (relative.Contains("{instance}", StringComparison.Ordinal))
                {
                    if (!System.Text.RegularExpressions.Regex.IsMatch(target.Spec.ConfigInputValue, "^[A-Za-z0-9_-]+$")) continue;
                    relative = relative.Replace("{instance}", target.Spec.ConfigInputValue, StringComparison.Ordinal);
                }
            }
            string file = Path.GetFullPath(Path.Combine(root, relative));
            if (!file.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("配置修复资源超出用户快照");
            if (!File.Exists(file)) continue;
            TaskConfigView.ValidatePath(file);
            if (new FileInfo(file).Length > 2 * 1024 * 1024) continue;
            byte[] before = File.ReadAllBytes(file);
            ConfigRepairProposal? proposal = ConfigRepairPolicy.Propose(rule, target.Script.PluginType,
                target.Spec.PluginVersion, target.UserKey, scriptId, identity, metadata.ConfigLocatorHash,
                metadata.Generation, before, context, out byte[]? after, target.Spec.ConfigEditor,
                id => ReadRepairResource(id, store, target.Spec, scriptId, target.UserKey));
            if (proposal is null || after is null) continue;
            proposal = proposal with { Token = ConfigRepairPolicy.Token(target.Script.PluginType, target.Spec.PluginVersion,
                target.UserKey, scriptId, identity, metadata.ConfigLocatorHash, metadata.Generation, before, rule) };
            return OperationResult<RepairCandidate>.Ok(new(target, metadata, root, file,
                extraSite.Length == 0 ? null : extraSite, before, after, proposal));
        }
        return Validation<RepairCandidate>("没有需要修复的已声明配置项", "config_repair_none");
    }

    private static System.Text.Json.Nodes.JsonNode? ReadRepairResource(string id, string store,
        ResolvedScriptSpec spec, string scriptId, string userKey)
    {
        string root = store, relative;
        if (id == "extra:0" && spec.ExtraConfigPaths.Count > 0)
        {
            string site = spec.ExtraConfigPaths[0];
            root = ConfigPaths.StoreExtraDir(scriptId, userKey, site);
            relative = Path.GetFileName(site);
        }
        else if (id.StartsWith("config:", StringComparison.Ordinal)) relative = id[7..];
        else throw new InvalidDataException("配置修复读取未授权资源");
        string file = Path.GetFullPath(Path.Combine(root, relative));
        if (!file.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("配置修复读取超出当前用户快照");
        if (!File.Exists(file)) return null;
        TaskConfigView.ValidatePath(file);
        if (new FileInfo(file).Length > 2 * 1024 * 1024) throw new InvalidDataException("配置修复读取超出大小限制");
        string format = Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".json" => "json", ".yml" or ".yaml" => "yaml",
            _ => throw new InvalidDataException("配置修复读取不支持的格式"),
        };
        return new TaskConfigDocument(File.ReadAllBytes(file), format).Document;
    }

    private static void CopyRepairSnapshot(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            TaskConfigView.ValidatePath(file);
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private sealed class ConfigRepairDisabledException : Exception { }
}
