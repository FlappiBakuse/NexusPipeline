using NexusPipeline.App.Abstractions;
using NexusPipeline.App.Contracts;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services;
using NexusPipeline.Services.Execution;
using NexusPipeline.Utilities;

namespace NexusPipeline.App.Commands;

internal static partial class UserCommands
{
    private const int MaxAvatarBytes = 5 * 1024 * 1024;

    public static OperationResult<bool> SetAvatar(string userId, string? mimeType, byte[]? data)
    {
        if (RuntimeContext.Instance.EntityState.FindUser(userId) is null)
        {
            return NotFound<bool>($"未找到用户：{userId}");
        }
        string mime = mimeType?.Trim().ToLowerInvariant() ?? "";
        string extension = mime switch
        {
            "image/png" => "png",
            "image/jpeg" => "jpg",
            "image/webp" => "webp",
            _ => "",
        };
        if (extension.Length == 0 || data is null || data.Length == 0
            || data.Length > MaxAvatarBytes || !HasMatchingMagic(mime, data))
        {
            return Validation<bool>("头像文件格式或大小不符合要求（上限 5 MiB）");
        }
        try
        {
            string dir = Path.Combine(AppPaths.UserAssetsDir, userId);
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, "avatar." + extension);
            File.WriteAllBytes(target, data);
            foreach (string file in Directory.GetFiles(dir, "avatar.*"))
            {
                if (!string.Equals(file, target, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(file);
                }
            }
            return OperationResult<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Internal<bool>(ex);
        }
    }

    public static OperationResult<bool> RemoveAvatar(string userId)
    {
        if (RuntimeContext.Instance.EntityState.FindUser(userId) is null)
        {
            return NotFound<bool>($"未找到用户：{userId}");
        }
        try
        {
            DeleteAvatarFiles(userId);
            return OperationResult<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Internal<bool>(ex);
        }
    }

}
