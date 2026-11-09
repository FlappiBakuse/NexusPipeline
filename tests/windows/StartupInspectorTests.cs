using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml;
using System.Xml.Linq;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Windows;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class StartupInspectorTests
{
    [Fact(DisplayName = "Native Task Scheduler Validates A Frozen Highest Privilege Logon Task Without Registering Or Starting It")]
    public void InspectsFrozenTasks()
    {
        var definition = Definition(["serve", "--runtime-root", Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "startup-fixture"), "--port", "58731"]);
        IStartupRegistrationInspector native = new StartupRegistrationEffects();
        Assert.Equal(StartupRegistrationState.Missing, native.Inspect(definition).State);
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) native.ValidateOnly(definition);
        else Assert.Equal("startup_registration_elevation_required",
            Assert.Throws<HostSettingsException>(() => native.ValidateOnly(definition)).Message);
        Assert.Equal(StartupRegistrationState.Missing, native.Inspect(definition).State);
        var xml = StartupTaskXml.Encode(definition);
        using var scheduler = new WindowsTaskScheduler();
        var wanted = StartupTaskXml.Read(scheduler.Normalize(xml));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var expectedDescriptor = $"D:P(A;;GA;;;SY)(A;;GA;;;{definition.UserSid})";
        Assert.Equal("HighestAvailable", wanted.Descendants(ns + "RunLevel").Single().Value);
        Assert.Equal("InteractiveToken", wanted.Descendants(ns + "LogonType").Single().Value);
        Assert.Single(wanted.Descendants(ns + "LogonTrigger"));
        Assert.Equal($"nexus.startup/1/{definition.InstanceId:D}/{definition.RegistrationId:D}", wanted.Descendants(ns + "Source").Single().Value);
        Assert.Equal("true", wanted.Descendants(ns + "UseUnifiedSchedulingEngine").Single().Value);
        Assert.Equal(expectedDescriptor, wanted.Descendants(ns + "SecurityDescriptor").Single().Value);
        var document = StartupTaskXml.Read(xml);
        var logonUser = document.Descendants(ns + "LogonTrigger").Single().Element(ns + "UserId")!;
        logonUser.Value = ((NTAccount)new SecurityIdentifier(definition.UserSid).Translate(typeof(NTAccount))).Value;
        Assert.True(XNode.DeepEquals(wanted, StartupTaskXml.Read(scheduler.Normalize(document.ToString()))));
        logonUser.Value = "S-1-5-18";
        Assert.False(XNode.DeepEquals(wanted, StartupTaskXml.Read(scheduler.Normalize(document.ToString()))));
        logonUser.Value = definition.UserSid;
        var descriptor = document.Descendants(ns + "SecurityDescriptor").Single();
        descriptor.Value += "(A;;GA;;;WD)";
        Assert.False(XNode.DeepEquals(wanted, StartupTaskXml.Read(scheduler.Normalize(document.ToString()))));
        descriptor.Value = expectedDescriptor;
        document.Descendants(ns + "UseUnifiedSchedulingEngine").Single().Value = "false";
        Assert.False(XNode.DeepEquals(wanted, StartupTaskXml.Read(scheduler.Normalize(document.ToString()))));
    }

    [Theory(DisplayName = "Startup Xml Arguments Round Trip Through The Actual Windows Parser Without Launching A Process")]
    [InlineData("")]
    [InlineData("中文 & portable path")]
    [InlineData("D:\\example path\\tail\\")]
    [InlineData("a\\\"b\\\\")]
    public void ParsesStartupArguments(string argument)
    {
        var definition = Definition([argument, "--fixture"]);
        var document = StartupTaskXml.Read(StartupTaskXml.Encode(definition));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var commandLine = "host.exe " + document.Descendants(ns + "Arguments").Single().Value;
        var pointer = CommandLineToArgv(commandLine, out var count);
        Assert.NotEqual(IntPtr.Zero, pointer);
        try
        {
            var arguments = Enumerable.Range(0, count).Select(index => Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, index * IntPtr.Size))).ToArray();
            Assert.Equal(new[] { "host.exe", argument, "--fixture" }, arguments);
        }
        finally { _ = LocalFree(pointer); }
    }

    [Fact(DisplayName = "Startup Xml Rejects An External Entity Before It Can Become An Inspected Definition")]
    public void RejectsXmlEntities()
    {
        const string xml = "<!DOCTYPE Task [<!ENTITY external SYSTEM 'file:///D:/private'>]><Task xmlns='http://schemas.microsoft.com/windows/2004/02/mit/task'>&external;</Task>";
        Assert.Throws<XmlException>(() => StartupTaskXml.Read(xml));
    }

    private static StartupRegistrationDefinition Definition(ImmutableArray<string> arguments)
    {
        var identity = ProcessIdentityReader.Read(Environment.ProcessId);
        return new(Guid.NewGuid(), Guid.NewGuid(), identity.UserSid,
            Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!, arguments,
            Path.GetDirectoryName(identity.ImagePath)!, DateTimeOffset.UtcNow);
    }

    [DllImport("shell32.dll", EntryPoint = "CommandLineToArgvW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgv(string commandLine, out int count);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr pointer);
}
