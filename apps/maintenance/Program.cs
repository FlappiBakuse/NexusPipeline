if (OperatingSystem.IsWindows() && args is ["installer-state", ..])
    return NexusPipeline.Maintenance.InstallerEntry.Run(args);
if (OperatingSystem.IsWindows()) return await NexusPipeline.Maintenance.MaintenanceApplication.RunAsync(args);
Console.Error.WriteLine("maintenance_windows_required");
return 2;
