using System.Text;
using NexusPipeline.Persistence;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Plugins.Managed;
using Xunit;

namespace NexusPipeline.Tests;

/// <summary>宿主持有的通用插件二进制资产存储：作用域隔离、内容寻址、边界校验与原子写入。</summary>
public sealed class PluginAssetStoreTests
{
    [Fact]
    public void Scopes_AreIsolatedWithinOnePluginNamespace()
    {
        using var fixture = new AssetStoreFixture();
        PluginAssetStore store = fixture.CreateStore("fixture-assets");

        PluginAssetInfo first = WriteText(store, "wallpapers", "png", "alpha");
        PluginAssetInfo second = WriteText(store, "attachments", "png", "beta");

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal("wallpapers", first.Scope);
        Assert.Equal("attachments", second.Scope);
        Assert.Single(List(store, "wallpapers"));
        Assert.Single(List(store, "attachments"));
        Assert.True(store.DeleteAsync("attachments", second.Id).AsTask().GetAwaiter().GetResult());
        Assert.Null(store.OpenAsync("wallpapers", second.Id).AsTask().GetAwaiter().GetResult());
        Assert.Equal("alpha", ReadText(store, "wallpapers", first.Id));
        Assert.Empty(List(store, "attachments"));
    }

    [Fact]
    public void WriteReadDeleteAndList_RoundTripAssetMetadata()
    {
        using var fixture = new AssetStoreFixture();
        PluginAssetStore store = fixture.CreateStore("fixture-assets");

        PluginAssetInfo written = WriteText(store, "wallpapers", ".JPG", "content-one");
        Assert.Equal(64, written.Id.Length);
        Assert.Equal("wallpapers", written.Scope);
        Assert.Equal("jpg", written.Extension);
        Assert.Equal("content-one".Length, written.SizeBytes);
        Assert.True(File.Exists(fixture.AssetPath("fixture-assets", "wallpapers", written.Id, "jpg")));

        using (PluginAssetContent? opened = store.OpenAsync("wallpapers", written.Id).AsTask().GetAwaiter().GetResult())
        {
            Assert.NotNull(opened);
            using var reader = new StreamReader(opened!.Content, Encoding.UTF8);
            Assert.Equal("content-one", reader.ReadToEnd());
            Assert.Equal(written.Id, opened.Info.Id);
            Assert.Equal("jpg", opened.Info.Extension);
        }

        Assert.Null(store.OpenAsync("wallpapers", new string('0', 64)).AsTask().GetAwaiter().GetResult());
        Assert.True(store.DeleteAsync("wallpapers", written.Id).AsTask().GetAwaiter().GetResult());
        Assert.Empty(List(store, "wallpapers"));
        Assert.False(store.DeleteAsync("wallpapers", written.Id).AsTask().GetAwaiter().GetResult());
    }

    [Fact]
    public void List_OrdersAssetsByCreationTimeThenId()
    {
        using var fixture = new AssetStoreFixture();
        PluginAssetStore store = fixture.CreateStore("fixture-assets");

        PluginAssetInfo first = WriteText(store, "wallpapers", "png", "first");
        Thread.Sleep(20);
        PluginAssetInfo second = WriteText(store, "wallpapers", "png", "second");

        IReadOnlyList<PluginAssetInfo> items = List(store, "wallpapers");

        Assert.Equal(new[] { first.Id, second.Id }, items.Select(item => item.Id).ToArray());
    }

    [Fact]
    public void Write_IsContentAddressed_AndReturnsTheSameIdForIdenticalContent()
    {
        using var fixture = new AssetStoreFixture();
        PluginAssetStore store = fixture.CreateStore("fixture-assets");

        PluginAssetInfo first = WriteText(store, "wallpapers", "png", "same-bytes");
        PluginAssetInfo second = WriteText(store, "wallpapers", "png", "same-bytes");

        Assert.Equal(first.Id, second.Id);
        Assert.Single(List(store, "wallpapers"));
        Assert.Single(Directory.GetFiles(fixture.AssetDir("fixture-assets", "wallpapers")));

        PluginAssetInfo third = WriteText(store, "wallpapers", "png", "other-bytes");
        Assert.NotEqual(first.Id, third.Id);
        Assert.Equal(2, List(store, "wallpapers").Count);
    }

    [Fact]
    public void Write_RejectsUnsafeExtensionAndUnsafeAssetIdLookups()
    {
        using var fixture = new AssetStoreFixture();
        PluginAssetStore store = fixture.CreateStore("fixture-assets");

        Assert.Throws<ArgumentException>("extension", () => WriteText(store, "wallpapers", "p n g", "payload"));
        Assert.Throws<ArgumentException>("extension", () => WriteText(store, "wallpapers", "../png", "payload"));
        Assert.Throws<ArgumentException>("extension", () => WriteText(store, "wallpapers", "", "payload"));
        Assert.Throws<ArgumentException>("extension", () => WriteText(store, "wallpapers", new string('a', 13), "payload"));
        Assert.Throws<ArgumentException>("assetId", () => store.OpenAsync("wallpapers", "../escape").AsTask().GetAwaiter().GetResult());
        Assert.Throws<ArgumentException>("assetId", () => store.OpenAsync("wallpapers", new string('z', 64)).AsTask().GetAwaiter().GetResult());
        Assert.Throws<ArgumentException>("assetId", () => store.DeleteAsync("wallpapers", "not-a-hash").AsTask().GetAwaiter().GetResult());
        // 资产 Id 大小写不敏感：大写十六进制按小写内容寻址解析。
        Assert.Null(store.OpenAsync("wallpapers", new string('A', 64)).AsTask().GetAwaiter().GetResult());
    }

    [Fact]
    public void ScopeLookups_RejectEscapingScopesWithoutTouchingTheRoot()
    {
        using var fixture = new AssetStoreFixture();
        PluginAssetStore store = fixture.CreateStore("fixture-assets");

        Assert.Throws<ArgumentException>(() => WriteText(store, "../outside", "png", "payload"));
        Assert.Throws<ArgumentException>(() => WriteText(store, "wallpapers/../../outside", "png", "payload"));
        Assert.Throws<ArgumentException>(() => WriteText(store, "wallpapers\\nested", "png", "payload"));
        Assert.Throws<ArgumentException>(() => WriteText(store, Path.Combine(fixture.Root, "absolute"), "png", "payload"));
        Assert.Throws<ArgumentException>(() => List(store, ""));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "outside")));
    }

    [Fact]
    public void Write_RejectsContentAboveTheHostAssetLimit()
    {
        using var fixture = new AssetStoreFixture();
        PluginAssetStore store = fixture.CreateStore("fixture-assets");
        using var oversized = new MemoryStream(new byte[PluginAssetStore.MaxAssetBytes + 1]);

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => store.WriteAsync("wallpapers", "png", oversized).AsTask().GetAwaiter().GetResult());

        Assert.Contains("上限", error.Message);
        Assert.Empty(List(store, "wallpapers"));
        Assert.Empty(Directory.GetFiles(fixture.AssetDir("fixture-assets", "wallpapers"), "*.part"));
    }

    [Fact]
    public void Write_RejectsEmptyContent()
    {
        using var fixture = new AssetStoreFixture();
        PluginAssetStore store = fixture.CreateStore("fixture-assets");

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => WriteText(store, "wallpapers", "png", ""));

        Assert.Contains("空", error.Message);
        Assert.Empty(List(store, "wallpapers"));
    }

    [Fact]
    public void Write_LeavesNoTemporaryResidueAfterSuccessOrFailure()
    {
        using var fixture = new AssetStoreFixture();
        PluginAssetStore store = fixture.CreateStore("fixture-assets");
        string directory = fixture.AssetDir("fixture-assets", "wallpapers");

        WriteText(store, "wallpapers", "png", "payload");
        Assert.Empty(Directory.GetFiles(directory, "*.part"));

        using var failing = new ThrowingStream();
        Assert.Throws<IOException>(() => store.WriteAsync("wallpapers", "png", failing).AsTask().GetAwaiter().GetResult());
        Assert.Empty(Directory.GetFiles(directory, "*.part"));
        Assert.Single(List(store, "wallpapers"));
    }

    private static PluginAssetInfo WriteText(PluginAssetStore store, string scope, string extension, string content)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        return store.WriteAsync(scope, extension, stream).AsTask().GetAwaiter().GetResult();
    }

    private static IReadOnlyList<PluginAssetInfo> List(PluginAssetStore store, string scope)
    {
        return store.ListAsync(scope).AsTask().GetAwaiter().GetResult();
    }

    private static string ReadText(PluginAssetStore store, string scope, string assetId)
    {
        using PluginAssetContent? opened = store.OpenAsync(scope, assetId).AsTask().GetAwaiter().GetResult();
        Assert.NotNull(opened);
        using var reader = new StreamReader(opened!.Content, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>写入中途失败的源流：验证失败路径不留下 .part 残留。</summary>
    private sealed class ThrowingStream : Stream
    {
        private bool _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("读取失败");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_read)
            {
                _read = true;
                buffer.Span[0] = 1;
                return ValueTask.FromResult(1);
            }
            throw new IOException("读取失败");
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class AssetStoreFixture : IDisposable
    {
        private const string PluginName = "fixture-assets";

        public AssetStoreFixture()
        {
            Root = Path.Combine(AppPaths.ConfigDir, "plugins", PluginName, "assets");
            RemovePluginRoot();
        }

        /// <summary>插件资产的生产根目录：config/plugins/{插件名}/assets。</summary>
        public string Root { get; }

        public PluginAssetStore CreateStore(string pluginName) => new(pluginName);

        public string AssetDir(string pluginName, string scope)
        {
            return Path.Combine(AppPaths.ConfigDir, "plugins", pluginName, "assets", scope);
        }

        public string AssetPath(string pluginName, string scope, string assetId, string extension)
        {
            return Path.Combine(AssetDir(pluginName, scope), assetId + "." + extension);
        }

        public void Dispose() => RemovePluginRoot();

        private static void RemovePluginRoot()
        {
            string pluginRoot = Path.Combine(AppPaths.ConfigDir, "plugins", PluginName);
            if (Directory.Exists(pluginRoot))
            {
                Directory.Delete(pluginRoot, recursive: true);
            }
        }
    }
}
