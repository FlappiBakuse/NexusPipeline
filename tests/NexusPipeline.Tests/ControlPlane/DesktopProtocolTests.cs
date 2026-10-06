using System.IO.Pipes;
using System.Runtime.InteropServices;
using NexusPipeline.Host.Desktop;
using NexusPipeline.Platform.Windows;
using Xunit;

namespace NexusPipeline.Tests.ControlPlane;

public sealed class DesktopProtocolTests
{
    [Fact]
    public void ProcessIdentityIsStableBeforeTheImageLoaderRuns()
    {
        string image = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        Assert.True(CreateProcessW(image, null, IntPtr.Zero, IntPtr.Zero, false, 0x08000004,
            IntPtr.Zero, null, ref startup, out ProcessInformation child),
            "Cannot create owned suspended process: " + Marshal.GetLastWin32Error());
        try
        {
            DesktopProcessIdentity initial = DesktopProcessIdentity.Read(checked((int)child.Pid));
            Assert.Equal(Path.GetFullPath(image), initial.ExecutablePath, ignoreCase: true);
            Assert.Equal(initial, DesktopProcessIdentity.Read(initial.Pid));
            Assert.True(initial.IsAlive());
            Assert.False(initial.HasExited());
        }
        finally
        {
            bool terminated = TerminateProcess(child.Process, 0);
            uint wait = terminated ? WaitForSingleObject(child.Process, 5000) : uint.MaxValue;
            CloseHandle(child.Thread);
            CloseHandle(child.Process);
            Assert.True(terminated && wait == 0, "Owned suspended process did not exit");
        }
    }

    [Fact]
    public void AuthenticationBindsBothNoncesAndCannotReflectDirection()
    {
        byte[] key = Enumerable.Repeat((byte)42, 32).ToArray();
        string host = DesktopSupervisorProtocol.Proof(key, "host", new('a', 64), new('b', 32), new('c', 64), new('d', 64), new('e', 32), new('f', 64));
        string client = DesktopSupervisorProtocol.Proof(key, "client", new('a', 64), new('b', 32), new('c', 64), new('d', 64), new('e', 32), new('f', 64));
        Assert.NotEqual(host, client);
        Assert.Equal("53544f697a62965c4195b0164f9b1c979b25045c2cbdf4aaab975d6893ff533b", host);
        Assert.Equal("6f874428fb969e711b8a38bc2e8dd263109f9771f87bc3ec46d60381067d7bb7", client);
        Assert.True(DesktopSupervisorProtocol.EqualsProof(host, host));
        Assert.False(DesktopSupervisorProtocol.EqualsProof(host, client));
        Assert.False(DesktopSupervisorProtocol.EqualsProof(new('G', 64), host));
    }

    [Fact]
    public async Task RealNamedPipeUsesKernelPeerIdentityAndBoundedFrames()
    {
        string name = "NexusPipeline.Test.Protocol." + Guid.NewGuid().ToString("N");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using NamedPipeServerStream server = DesktopPipeTransport.Create(name);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Task waiting = server.WaitForConnectionAsync(deadline.Token);
        await client.ConnectAsync(deadline.Token);await waiting;
        Assert.Equal(Environment.ProcessId, NamedPipePeerIdentity.Client(server).Pid);
        Assert.Equal(DesktopProcessIdentity.Read(Environment.ProcessId), NamedPipePeerIdentity.Server(client));
        await DesktopPipeTransport.WriteAsync(client, new { type = "ping", requestId = "case", data = new { } }, deadline.Token);
        var frame = await DesktopPipeTransport.ReadAsync(server, deadline.Token);
        Assert.Equal("ping", frame.GetProperty("type").GetString());
        await client.WriteAsync(new byte[] { 1, 0, 1, 0 }, deadline.Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => DesktopPipeTransport.ReadAsync(server, deadline.Token));
    }

    [Fact]
    public void ExplicitStartupAndRestoreIntentsOverrideThePreference()
    {
        Assert.True(DesktopLaunchOptions.Parse([]).ShouldShow(false, false));
        Assert.False(DesktopLaunchOptions.Parse(["service", "--background"]).ShouldShow(true, true));
        Assert.False(DesktopLaunchOptions.Parse(["service"]).ShouldShow(false, true));
        Assert.True(DesktopLaunchOptions.Parse(["service"]).ShouldShow(true, false));
        Assert.False(DesktopLaunchOptions.Parse(["restart"]).ShouldShow(true, false));
        Assert.True(DesktopLaunchOptions.Parse(["restart"]).ShouldShow(false, true));
    }

    [Fact]
    public void UnknownSessionFilesRetainTheirBytes()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-desktop-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] original = [255, 0, 42, 13, 10];
            File.WriteAllBytes(Path.Combine(root, "session.json"), original);
            File.WriteAllBytes(Path.Combine(root, "session.key"), original);
            var store = new DesktopSessionStore(root, new('a', 64));
            Assert.Null(store.Load());
            Assert.Throws<InvalidDataException>(() => store.CreateKey());
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(root, "session.json")));
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(root, "session.key")));
        }
        finally { Directory.Delete(root, true); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public uint X, Y, Width, Height, Columns, Rows, Fill, Flags;
        public ushort Show, ReservedSize;
        public IntPtr ReservedBytes, Input, Output, Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public uint Pid, ThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string application, string? commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string? directory,
        ref StartupInfo startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
