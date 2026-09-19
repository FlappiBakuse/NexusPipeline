using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace NexusPipeline.Platform.Windows;

/// <summary>从 Windows PE 资源读取最高分辨率图标，并在失败时回退系统关联图标。</summary>
internal sealed class ExecutableIconReader
{
    private const uint LoadLibraryAsDataFile = 0x2;
    private const uint LoadLibraryAsImageResource = 0x20;
    private const int RtGroupIcon = 14;
    private const int RtIcon = 3;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryEx(string fileName, IntPtr file, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr module);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool EnumResourceNames(IntPtr module, IntPtr type, EnumResourceNameProc callback, IntPtr param);

    private delegate bool EnumResourceNameProc(IntPtr module, IntPtr type, IntPtr name, IntPtr param);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SizeofResource(IntPtr module, IntPtr resource);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LockResource(IntPtr resource);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconFromResourceEx(byte[] data, uint bytes, bool icon, uint version, int cxDesired, int cyDesired, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    public byte[]? Read(string mainExe)
    {
        byte[]? icon = ExtractBestIcon(mainExe);
        if (icon is not null)
        {
            return icon;
        }
        if (Path.GetExtension(mainExe).ToLowerInvariant() is ".bat" or ".cmd" or ".com")
        {
            return null;
        }
        try
        {
            using Icon? associated = Icon.ExtractAssociatedIcon(mainExe);
            if (associated is null)
            {
                return null;
            }
            using Bitmap bitmap = associated.ToBitmap();
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>PE 资源枚举最高分辨率图标（RT_GROUP_ICON → GRPICONDIR → 最大尺寸条目）。</summary>
    private static byte[]? ExtractBestIcon(string mainExe)
    {
        IntPtr module = IntPtr.Zero;
        IntPtr iconHandle = IntPtr.Zero;
        try
        {
            module = LoadLibraryEx(mainExe, IntPtr.Zero, LoadLibraryAsDataFile | LoadLibraryAsImageResource);
            if (module == IntPtr.Zero)
            {
                return null;
            }
            int bestWidth = 0;
            int bestHeight = 0;
            byte[]? bestDirectory = null;
            int bestIconId = 0;
            var groupNames = new List<IntPtr>();
            EnumResourceNames(module, (IntPtr)RtGroupIcon, (_, _, name, _) =>
            {
                groupNames.Add(name);
                return true;
            }, IntPtr.Zero);
            foreach (IntPtr groupName in groupNames)
            {
                IntPtr resource = FindResource(module, groupName, (IntPtr)RtGroupIcon);
                if (resource == IntPtr.Zero)
                {
                    continue;
                }
                byte[]? directory = ReadBytes(module, resource);
                if (directory is null || directory.Length < 6)
                {
                    continue;
                }
                int count = BitConverter.ToUInt16(directory, 4);
                for (int index = 0; index < count; index++)
                {
                    int offset = 6 + index * 14;
                    if (offset + 14 > directory.Length)
                    {
                        break;
                    }
                    int width = directory[offset] == 0 ? 256 : directory[offset];
                    int height = directory[offset + 1] == 0 ? 256 : directory[offset + 1];
                    if (width * height > bestWidth * bestHeight)
                    {
                        bestWidth = width;
                        bestHeight = height;
                        bestDirectory = directory;
                        bestIconId = BitConverter.ToUInt16(directory, offset + 12);
                    }
                }
            }
            if (bestDirectory is null)
            {
                return null;
            }
            IntPtr iconResource = FindResource(module, (IntPtr)bestIconId, (IntPtr)RtIcon);
            if (iconResource == IntPtr.Zero)
            {
                return null;
            }
            byte[]? iconData = ReadBytes(module, iconResource);
            if (iconData is null || iconData.Length == 0)
            {
                return null;
            }
            iconHandle = CreateIconFromResourceEx(iconData, (uint)iconData.Length, true, 0x00030000, 0, 0, 0);
            if (iconHandle == IntPtr.Zero)
            {
                return null;
            }
            using Icon icon = Icon.FromHandle(iconHandle);
            using Bitmap bitmap = icon.ToBitmap();
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (iconHandle != IntPtr.Zero)
            {
                DestroyIcon(iconHandle);
            }
            if (module != IntPtr.Zero)
            {
                FreeLibrary(module);
            }
        }
    }

    private static byte[]? ReadBytes(IntPtr module, IntPtr resource)
    {
        uint size = SizeofResource(module, resource);
        if (size == 0 || size > 8 * 1024 * 1024)
        {
            return null;
        }
        IntPtr loaded = LoadResource(module, resource);
        if (loaded == IntPtr.Zero)
        {
            return null;
        }
        IntPtr pointer = LockResource(loaded);
        if (pointer == IntPtr.Zero)
        {
            return null;
        }
        var buffer = new byte[size];
        Marshal.Copy(pointer, buffer, 0, (int)size);
        return buffer;
    }
}
