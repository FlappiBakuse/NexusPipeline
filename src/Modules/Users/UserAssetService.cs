using NexusPipeline.Platform.Storage;
using NexusPipeline.Shared.Logging;

namespace NexusPipeline.Modules.Users;

/// <summary>User-owned asset storage; control-plane handlers consume only its results.</summary>
internal sealed class UserAssetService
{
    private const int MaxAvatarBytes = 5 * 1024 * 1024;

    internal bool TrySaveAvatar(string userId, string extension, byte[] data, out string? error)
    {
        error = null;
        try
        {
            string normalizedExtension = extension.Trim().ToLowerInvariant();
            if (normalizedExtension is not ("png" or "jpg" or "webp")
                || data.Length == 0
                || data.Length > MaxAvatarBytes)
            {
                error = "头像文件格式或大小不符合要求（上限 5 MiB）";
                return false;
            }
            string? dir = GetUserDirectory(userId);
            if (dir is null)
            {
                error = "用户头像路径无效";
                return false;
            }
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, "avatar." + normalizedExtension);
            File.WriteAllBytes(target, data);
            foreach (string file in Directory.GetFiles(dir, "avatar.*"))
            {
                if (!string.Equals(file, target, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(file);
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    internal void RemoveAvatar(string userId)
    {
        string? dir = GetUserDirectory(userId);
        if (dir is null || !Directory.Exists(dir))
        {
            return;
        }
        foreach (string file in Directory.GetFiles(dir, "avatar.*"))
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[警告] 清理用户头像失败（{file}）：{ex.Message}");
            }
        }
        try
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
            }
        }
        catch
        {
        }
    }

    internal bool HasAvatar(string userId) => FindAvatarPath(userId) is not null;

    internal UserAvatarContent? ReadAvatar(string userId)
    {
        string? path = FindAvatarPath(userId);
        if (path is null)
        {
            return null;
        }
        byte[] data = File.ReadAllBytes(path);
        if (data.Length == 0 || data.Length > MaxAvatarBytes)
        {
            return null;
        }
        string contentType = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" => "image/jpeg",
            _ => "image/webp",
        };
        return new UserAvatarContent(contentType, data);
    }

    private static string? FindAvatarPath(string userId)
    {
        string? dir = GetUserDirectory(userId);
        if (dir is null || !Directory.Exists(dir))
        {
            return null;
        }
        return Directory.GetFiles(dir, "avatar.*").FirstOrDefault(path =>
            Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(path).Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(path).Equals(".webp", StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetUserDirectory(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId) || Path.GetFileName(userId) != userId)
        {
            return null;
        }
        string root = Path.GetFullPath(AppPaths.UserAssetsDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string directory = Path.GetFullPath(Path.Combine(root, userId));
        return directory.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? directory
            : null;
    }
}

internal sealed record UserAvatarContent(string ContentType, byte[] Data);
