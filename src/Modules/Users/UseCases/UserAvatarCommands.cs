using NexusPipeline.Platform.Storage;
using NexusPipeline.Shared.Results;

namespace NexusPipeline.Modules.Users.UseCases;

internal sealed partial class UserCommands
{
    private const int MaxAvatarBytes = 5 * 1024 * 1024;

    public OperationResult<bool> SetAvatar(string userId, string? mimeType, byte[]? data)
    {
        if (_state.Find(userId) is null)
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
            return _assets.TrySaveAvatar(userId, extension, data, out string? saveError)
                ? OperationResult<bool>.Ok(true)
                : Internal<bool>(new IOException(saveError ?? "头像保存失败"));
        }
        catch (Exception ex)
        {
            return Internal<bool>(ex);
        }
    }

    public OperationResult<bool> RemoveAvatar(string userId)
    {
        if (_state.Find(userId) is null)
        {
            return NotFound<bool>($"未找到用户：{userId}");
        }
        try
        {
            _assets.RemoveAvatar(userId);
            return OperationResult<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Internal<bool>(ex);
        }
    }

}
