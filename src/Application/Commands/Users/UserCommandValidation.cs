using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services;
using NexusPipeline.Utilities;

namespace NexusPipeline.App.Commands;

/// <summary>用户输入与头像资源验证。</summary>
internal static partial class UserCommands
{
    private sealed record ValidationIssue(
        string Code,
        string Message,
        IReadOnlyDictionary<string, object?>? Args = null);

    private static ValidationIssue? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || !UserNameRule.IsValidName(name.Trim()))
        {
            return new ValidationIssue("user_name_invalid", "用户名不能为空且不能包含非法字符");
        }
        string? lengthError = Limits.CheckNameBytes(name.Trim(), AppFixedLimits.MaxEntityNameBytes, "用户名");
        return lengthError is null ? null : new ValidationIssue("user_name_invalid", lengthError);
    }

    private static ValidationIssue? ValidateRemark(string? remark)
    {
        string? lengthError = Limits.CheckNameBytes(remark?.Trim() ?? "", AppFixedLimits.MaxUserRemarkBytes, "备注");
        return lengthError is null ? null : new ValidationIssue("user_remark_invalid", lengthError);
    }

    private static string? ValidateRunDays(int value) => Limits.CheckRunDays(value);

    private static string? ValidateMaxSuccessfulRunsPerDay(int value) =>
        Limits.CheckMaxSuccessfulRunsPerDay(value);

    private static string? ValidateSmtp(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : SmtpSender.ValidateRecipients(value.Trim());

    private static void DeleteAvatarFiles(string userId)
    {
        string dir = Path.Combine(AppPaths.UserAssetsDir, userId);
        if (!Directory.Exists(dir))
        {
            return;
        }
        foreach (string file in Directory.GetFiles(dir, "avatar.*"))
        {
            try { File.Delete(file); } catch (Exception ex) { Logger.Warn($"[警告] 清理用户头像失败（{file}）：{ex.Message}"); }
        }
        try
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch { }
    }

    private static bool HasMatchingMagic(string mime, byte[] data)
    {
        return mime switch
        {
            "image/png" => data.Length >= 8
                && data.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            "image/jpeg" => data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF,
            "image/webp" => data.Length >= 12
                && data.AsSpan(0, 4).SequenceEqual("RIFF"u8)
                && data.AsSpan(8, 4).SequenceEqual("WEBP"u8),
            _ => false,
        };
    }
}
