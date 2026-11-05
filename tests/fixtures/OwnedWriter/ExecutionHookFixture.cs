using System.Text.Json;

internal static class ExecutionHookFixture
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is not [var role, var marker, var configuration, var exitText, var waitText] ||
            role is not ("pre" or "main" or "post") || !int.TryParse(exitText, out var exit) || !bool.TryParse(waitText, out var wait))
            return 94;
        OwnedFixtureEvidence.RequirePath(marker);
        if (configuration != "-") OwnedFixtureEvidence.RequirePath(configuration);
        if (configuration != "-")
        {
            using var current = JsonDocument.Parse(await File.ReadAllTextAsync(configuration));
            if (role == "pre" && !current.RootElement.GetProperty("fixture").GetBoolean()) return 95;
            if (role != "pre" && current.RootElement.GetProperty("stage").GetString() != (role == "main" ? "pre" : "main")) return 96;
            await File.WriteAllTextAsync(configuration, JsonSerializer.Serialize(new { fixture = false, stage = role }));
        }
        await BindingOutputFixture.WriteAsync(role, marker);
        Console.WriteLine(role == "main" ? "done" : "failed");
        Console.Out.Flush();
        var ready = marker + "." + role;
        await File.WriteAllTextAsync(ready + ".prepared", role);
        File.Move(ready + ".prepared", ready, overwrite: true);
        if (wait)
            while (!File.Exists(ready + ".release")) await Task.Delay(10);
        return exit;
    }
}
