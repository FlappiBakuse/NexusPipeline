if (OperatingSystem.IsWindows())
    return NexusPipeline.Maintenance.InstallerEntry.Run(args, Environment.ProcessPath ?? throw new IOException("installer_image_unavailable"));
Console.Error.WriteLine("installer_windows_required");
return 2;
