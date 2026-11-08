using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NexusPipeline.Extensibility.Emulation;
using Xunit;

namespace NexusPipeline.StorageTests;

internal sealed class EmulatorInventoryFixture(string vendor) : IEmulatorInventory
{
    private readonly JsonElement fixture = ReadFixture(vendor);
    internal int Reads;
    internal int Port => fixture.GetProperty("port").GetInt32();
    public ValueTask<IReadOnlyList<EmulatorInstallation>> DiscoverAsync(EmulatorLayout layout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); Reads++;
        if (layout.Vendor != vendor) return ValueTask.FromResult<IReadOnlyList<EmulatorInstallation>>([]);
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "controlled-emulator", vendor.ToString());
        EmulatorTool Tool(string name) => new(name, Path.Combine(root, name), "fixture-physical:" + name, new string('c', 64), 1);
        var configs = fixture.GetProperty("configurations").EnumerateArray()
            .Select(value => Config(value.GetProperty("path").GetString()!, value.GetProperty("text").GetString()!)).ToArray();
        return ValueTask.FromResult<IReadOnlyList<EmulatorInstallation>>([new("controlled-install", vendor, root, "controlled-root",
            Tool(fixture.GetProperty("control").GetString()!), Tool(fixture.GetProperty("adb").GetString()!), configs)]);
    }
    public ValueTask<EmulatorCommandResult> EnumerateAsync(EmulatorInstallation installation, EmulatorCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.Equal(installation.Control.Id, command.ToolId);
        Assert.Equal(fixture.GetProperty("enumerate").GetString(), Assert.Single(command.Arguments));
        var output = fixture.GetProperty("stdout").GetString()!;
        return ValueTask.FromResult(new EmulatorCommandResult("completed", 0, output, "", "controlled-enumeration"));
    }
    private static EmulatorConfig Config(string path, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return new(path, path, "physical:" + path, Convert.ToHexStringLower(SHA256.HashData(bytes)), 1, bytes);
    }
    internal static JsonElement ReadFixture(string vendor)
    {
        var root = Environment.GetEnvironmentVariable("NexusPluginFixturesRoot") ?? throw new IOException("plugin_fixture_source_required");
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "storage", "emulator-inventory.json")));
        return document.RootElement.EnumerateObject().Single(item => item.Name.Equals(vendor, StringComparison.OrdinalIgnoreCase)).Value.Clone();
    }
}
