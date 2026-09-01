using NexusPipeline.Services;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class NativePathPickerServiceTests
{
    [Fact]
    public void IsExistingDirectory_rejects_empty_missing_and_file_paths()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-picker-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(root, "file.txt");
        Directory.CreateDirectory(root);
        File.WriteAllText(file, "test");
        try
        {
            Assert.False(NativePathPickerService.IsExistingDirectory(null));
            Assert.False(NativePathPickerService.IsExistingDirectory("  "));
            Assert.False(NativePathPickerService.IsExistingDirectory(Path.Combine(root, "missing")));
            Assert.False(NativePathPickerService.IsExistingDirectory(file));
            Assert.True(NativePathPickerService.IsExistingDirectory(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveInitialDirectory_uses_existing_directory_as_picker_start()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-picker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Equal(Path.GetFullPath(root), NativePathPickerService.ResolveInitialDirectory(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
