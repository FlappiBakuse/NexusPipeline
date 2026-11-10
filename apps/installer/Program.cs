if (OperatingSystem.IsWindows())
{
    var image = Environment.ProcessPath ?? throw new IOException("installer_image_unavailable");
    if (args is ["uninstall-open", .. var options])
        return await NexusPipeline.Installer.UninstallLaunch.RunAsync(image, options);
    return NexusPipeline.Installer.InstallerEntry.Run(args, image);
}
Console.Error.WriteLine("installer_windows_required");
return 2;
