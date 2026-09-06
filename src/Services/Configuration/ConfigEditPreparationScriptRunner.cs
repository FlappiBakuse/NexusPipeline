using NexusPipeline.Models;
using NexusPipeline.App.Abstractions;
using NexusPipeline.Plugins;
using NexusPipeline.Services;
using NexusPipeline.Services.Execution;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services.Configuration;

/// <summary>配置编辑准备脚本的受限执行入口。</summary>
internal static class ConfigEditPreparationScriptRunner
{
    internal static ConfigValidationResult Execute(
        ConfigEditorDescriptor descriptor,
        ScriptInstance script,
        ResolvedScriptUser user,
        ConfigSessionMark mark,
        string mode,
        string configInputName = "",
        string configInputValue = "")
    {
        string temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "NexusPipeline-config-editor-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temporaryRoot);
            var extras = new List<ConfigValidationExtraSnapshot>();
            foreach (ConfigSessionExtraPath entry in mark.ExtraConfigPaths)
            {
                PathKind kind = PathKindUtil.KindOf(entry.Path);
                extras.Add(new ConfigValidationExtraSnapshot(entry.Path, entry.Path)
                {
                    SingleFilePath = kind == PathKind.File ? entry.Path : null,
                    AllowWrite = true,
                });
            }

            var fields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["mode"] = mode,
                ["configInputName"] = mark.PendingConfigInput?.Name ?? configInputName,
                ["configInputValue"] = mark.PendingConfigInput?.Value ?? configInputValue,
            };
            if (string.IsNullOrWhiteSpace(fields["configInputValue"]))
            {
                fields["configInputValue"] = ResolveCurrentInputValue(script, mark);
            }

            var validatorDescriptor = new ConfigValidatorDescriptor(
                descriptor.PluginName,
                descriptor.PluginDirectory,
                descriptor.EditorPath,
                descriptor.Script);
            return ConfigValidationScriptRunner.ExecuteAsync(
                    validatorDescriptor,
                    script,
                    user,
                    temporaryRoot,
                    "config-edit-preparation",
                    extras,
                    allowMainWrites: false,
                    allowExtraWrites: true,
                    inputFields: fields)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            Logger.Warn($"[专项配置编辑:{descriptor.PluginName}] 准备脚本编排失败：{ex.Message}");
            return new ConfigValidationResult(
                true,
                "JavaScript 执行失败：" + ex.Message,
                Array.Empty<string>(),
                Array.Empty<ConfigValidationToast>(),
                Array.Empty<ConfigValidationNotification>());
        }
        finally
        {
            ConfigSwapPrimitives.TryDeleteDir(temporaryRoot);
        }
    }

    private static string ResolveCurrentInputValue(ScriptInstance script, ConfigSessionMark mark)
    {
        if (string.IsNullOrWhiteSpace(mark.ConfigPath))
        {
            return "";
        }
        return Path.GetFileNameWithoutExtension(mark.ConfigPath);
    }
}
