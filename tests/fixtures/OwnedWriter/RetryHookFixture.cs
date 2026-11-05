internal static class RetryHookFixture
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is not [var role, var marker, var exitText, var waitText] ||
            role is not ("pre" or "post") || !int.TryParse(exitText, out var exit) || !bool.TryParse(waitText, out var wait))
            return 94;
        OwnedFixtureEvidence.RequirePath(marker);
        await File.AppendAllTextAsync(marker + ".calls", role + "|" + Environment.ProcessId + Environment.NewLine);
        Console.WriteLine("failed");
        await Console.Out.FlushAsync();
        var ready = marker + "." + role;
        await File.WriteAllTextAsync(ready + ".prepared", Environment.CurrentDirectory);
        File.Move(ready + ".prepared", ready, overwrite: true);
        if (wait)
            while (!File.Exists(ready + ".release")) await Task.Delay(20);
        return exit;
    }
}
