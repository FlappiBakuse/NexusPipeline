using System.Text.Json;
using NexusPipeline.Modules.Updates;
using NexusPipeline.Platform.Storage;

try
{
    if (args.Length is not (1 or 2)) return 2;
    string root = Path.GetFullPath(args[0]);
    _ = ApplicationPayload.Validate(root, args.Length == 2 ? args[1] : null);
    byte[] record = BundleResourceReader.Read(Path.Combine(root, "NexusPipeline.exe"), "NexusPipeline.dll", "NexusPipeline.Desktop.BuildIdentity", ApplicationBuildIdentity.MaxBytes);
    Console.Write(System.Text.Encoding.UTF8.GetString(record));
    return 0;
}
catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
