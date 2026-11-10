return OperatingSystem.IsWindows() && args.Length == 0 ? NexusPipeline.Maintenance.UpdateDialog.Show() : 2;
