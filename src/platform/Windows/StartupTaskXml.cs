using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Platform.Windows;

public static class StartupTaskXml
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    public static string TaskName(StartupRegistrationDefinition value)
    {
        StartupRegistrationPolicy.Validate(value);
        return "NexusPipeline-" + value.InstanceId.ToString("D");
    }

    public static string Encode(StartupRegistrationDefinition value)
    {
        StartupRegistrationPolicy.Validate(value);
        var executable = PhysicalPathPolicy.NormalizeWithoutLinks(value.ExecutablePath);
        var directory = PhysicalPathPolicy.NormalizeWithoutLinks(value.WorkingDirectory);
        var task = new XElement(Ns + "Task", new XAttribute("version", "1.3"),
            new XElement(Ns + "RegistrationInfo",
                Element("Date", value.CreatedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
                Element("Author", value.UserSid), Element("URI", "\\" + TaskName(value)),
                Element("SecurityDescriptor", SecurityDescriptor(value.UserSid)),
                Element("Source", $"nexus.startup/1/{value.InstanceId:D}/{value.RegistrationId:D}")),
            new XElement(Ns + "Triggers", new XElement(Ns + "LogonTrigger", Element("Enabled", "true"), Element("UserId", value.UserSid))),
            new XElement(Ns + "Principals", new XElement(Ns + "Principal", new XAttribute("id", "Host"),
                Element("UserId", value.UserSid), Element("LogonType", "InteractiveToken"), Element("RunLevel", "HighestAvailable"))),
            new XElement(Ns + "Settings", Element("MultipleInstancesPolicy", "IgnoreNew"), Element("DisallowStartIfOnBatteries", "false"),
                Element("StopIfGoingOnBatteries", "false"), Element("AllowHardTerminate", "false"), Element("StartWhenAvailable", "true"),
                Element("RunOnlyIfNetworkAvailable", "false"), Element("AllowStartOnDemand", "true"), Element("Enabled", "true"),
                Element("Hidden", "false"), Element("RunOnlyIfIdle", "false"), Element("WakeToRun", "false"),
                Element("UseUnifiedSchedulingEngine", "true"), Element("ExecutionTimeLimit", "PT0S")),
            new XElement(Ns + "Actions", new XAttribute("Context", "Host"), new XElement(Ns + "Exec", Element("Command", executable),
                Element("Arguments", string.Join(' ', value.Arguments.Select(WindowsCommandLine.Quote))), Element("WorkingDirectory", directory))));
        return task.ToString(SaveOptions.DisableFormatting);
    }

    public static XDocument Read(string xml)
    {
        if (xml.Length is < 1 or > 131072) throw new IOException("startup_task_xml_size_invalid");
        using var input = new StringReader(xml);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 131072 });
        var document = XDocument.Load(reader);
        if (document.Root?.Name != Ns + "Task") throw new IOException("startup_task_xml_namespace_invalid");
        return document;
    }

    internal static string SecurityDescriptor(string userSid) => $"D:P(A;;GA;;;SY)(A;;GA;;;{userSid})";
    private static XElement Element(string name, string value) => new(Ns + name, value);
}
