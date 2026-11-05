using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Xml.Linq;
using NexusPipeline.Core.Settings;

namespace NexusPipeline.Platform.Windows;

[SupportedOSPlatform("windows")]
internal sealed class WindowsTaskScheduler : IStartupTaskScheduler
{
    private readonly object _service;
    private readonly object _folder;

    internal WindowsTaskScheduler()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new IOException("task_scheduler_unavailable");
        _service = Activator.CreateInstance(type) ?? throw new IOException("task_scheduler_unavailable");
        try { ((dynamic)_service).Connect(); _folder = ((dynamic)_service).GetFolder("\\"); }
        catch { Marshal.FinalReleaseComObject(_service); throw; }
    }

    public string? Read(string name)
    {
        object task;
        try { task = ((dynamic)_folder).GetTask(name); }
        catch (Exception error) when ((error is COMException or FileNotFoundException) && error.HResult == unchecked((int)0x80070002)) { return null; }
        try { return (string)((dynamic)task).Xml; }
        finally { Marshal.FinalReleaseComObject(task); }
    }

    public string Normalize(string xml)
    {
        object definition = ((dynamic)_service).NewTask(0);
        try
        {
            ((dynamic)definition).XmlText = xml;
            var document = StartupTaskXml.Read((string)((dynamic)definition).XmlText);
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            var root = document.Root!;
            var users = root.Element(ns + "Principals")?.Elements(ns + "Principal").Elements(ns + "UserId") ?? [];
            var logons = root.Element(ns + "Triggers")?.Elements(ns + "LogonTrigger").Elements(ns + "UserId") ?? [];
            // Registration can rewrite a logon SID to its account name; compare the actual Windows identity.
            foreach (var user in users.Concat(logons)) user.Value = UserSid(user.Value);
            return document.ToString(SaveOptions.DisableFormatting);
        }
        finally { Marshal.FinalReleaseComObject(definition); }
    }

    public void Validate(string name, string xml, string userSid)
    {
        Register(name, xml, userSid, 1, null);
        RequireElevation();
    }

    public void Create(string name, string xml, string userSid)
    {
        RequireElevation();
        var security = StartupTaskXml.SecurityDescriptor(userSid);
        Register(name, xml, userSid, 2, security);
    }

    private void Register(string name, string xml, string userSid, int flags, string? security)
    {
        const int InteractiveToken = 3;
        object? result = ((dynamic)_folder).RegisterTask(name, xml, flags, userSid, null, InteractiveToken, security);
        if (result is not null && Marshal.IsComObject(result)) Marshal.FinalReleaseComObject(result);
    }

    public void Delete(string name) => ((dynamic)_folder).DeleteTask(name, 0);

    private static void RequireElevation()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new HostSettingsException("startup_registration_elevation_required");
    }

    private static string UserSid(string value)
    {
        try
        {
            return value.StartsWith("S-", StringComparison.Ordinal)
                ? new SecurityIdentifier(value).Value
                : ((SecurityIdentifier)new NTAccount(value).Translate(typeof(SecurityIdentifier))).Value;
        }
        catch (Exception error) when (error is IdentityNotMappedException or ArgumentException)
        { throw new IOException("startup_task_user_identity_unresolved", error); }
    }

    public void Dispose()
    {
        Marshal.FinalReleaseComObject(_folder);
        Marshal.FinalReleaseComObject(_service);
    }
}
