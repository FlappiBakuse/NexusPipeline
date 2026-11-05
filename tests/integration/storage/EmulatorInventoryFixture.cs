using System.Security.Cryptography;
using System.Text;
using NexusPipeline.Extensibility.Emulation;
using Xunit;

namespace NexusPipeline.StorageTests;

internal sealed class EmulatorInventoryFixture(EmulatorVendor vendor) : IEmulatorInventory
{
    internal int Reads;
    internal int Port => vendor == EmulatorVendor.Nox ? 62001 : 5555;
    public ValueTask<IReadOnlyList<EmulatorInstallation>> DiscoverAsync(EmulatorLayout layout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); Reads++;
        if (layout.Vendor != vendor) return ValueTask.FromResult<IReadOnlyList<EmulatorInstallation>>([]);
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "controlled-emulator", vendor.ToString());
        var names = vendor switch { EmulatorVendor.LdPlayer => ("ldconsole.exe", "adb.exe"),
            EmulatorVendor.Nox => ("NoxConsole.exe", "nox_adb.exe"), _ => ("HD-Player.exe", "HD-Adb.exe") };
        EmulatorTool Tool(string name) => new(name, Path.Combine(root, name), "fixture-physical:" + name, new string('c', 64), 1);
        var configs = vendor switch {
            EmulatorVendor.Nox => new[] { Config("BignoxVMS/nox/nox.vbox", "<VirtualBox><Machine><Hardware><Network><Adapter><NAT><Forwarding name=\"adb\" proto=\"1\" hostport=\"62001\" guestport=\"5555\"/></NAT></Adapter></Network></Hardware></Machine></VirtualBox>") },
            EmulatorVendor.BlueStacks => [Config("bluestacks.conf", "bst.instance.Pie64.display_name=\"Same\"\nbst.instance.Pie64.adb_port=\"5555\"\n")], _ => [] };
        return ValueTask.FromResult<IReadOnlyList<EmulatorInstallation>>([new("controlled-install", vendor, root, "controlled-root", Tool(names.Item1), Tool(names.Item2), configs)]);
    }
    public ValueTask<EmulatorCommandResult> EnumerateAsync(EmulatorInstallation installation, EmulatorCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.Equal(installation.Control.Id, command.ToolId);
        Assert.Equal(vendor == EmulatorVendor.LdPlayer ? "list2" : "list", Assert.Single(command.Arguments));
        var output = vendor == EmulatorVendor.LdPlayer ? "0,Same,0,0,0,-1,-1\n" : "nox,Same,0,0,0,-1\n";
        return ValueTask.FromResult(new EmulatorCommandResult("completed", 0, output, "", "controlled-enumeration"));
    }
    private static EmulatorConfig Config(string path, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return new(path, path, "physical:" + path, Convert.ToHexStringLower(SHA256.HashData(bytes)), 1, bytes);
    }
}
