using NexusPipeline.Platform.Storage.Extensions;
using Xunit;

namespace NexusPipeline.ContractTests;

public sealed class ImageEncodingTests
{
    [Fact(DisplayName = "The Host Accepts The Exact Eight MiB Image Boundary And Rejects The Next Byte And Invalid Header Dimensions")]
    public void EnforcesImageBounds()
    {
        var original = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jC1sAAAAASUVORK5CYII=");
        var boundary = new byte[8388608];
        original.CopyTo(boundary, 0);
        var info = ExtensionImageBytes.Inspect("image/png", boundary);
        Assert.Equal(8388608, info.SizeBytes);
        Assert.Equal(1, info.Width);
        var oversize = new byte[8388609];
        boundary.CopyTo(oversize, 0);
        Assert.Equal("extension_image_size_invalid", Assert.Throws<IOException>(() => ExtensionImageBytes.Inspect("image/png", oversize)).Message);
        Assert.Equal("extension_image_format_invalid", Assert.Throws<IOException>(() => ExtensionImageBytes.Inspect("image/jpeg", original)).Message);
        boundary[18] = 0x20;
        boundary[19] = 1;
        Assert.Equal("extension_image_dimensions_invalid", Assert.Throws<IOException>(() => ExtensionImageBytes.Inspect("image/png", boundary)).Message);
        Assert.Equal("extension_image_type_invalid", Assert.Throws<IOException>(() => ExtensionImageBytes.Inspect("image/svg+xml", original)).Message);
    }
}
