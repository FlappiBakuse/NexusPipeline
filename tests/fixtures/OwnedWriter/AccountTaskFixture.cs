using System.Text;

internal static class AccountTaskFixture
{
    internal static async Task<int> RunAsync(string outcome, string marker)
    {
        OwnedFixtureEvidence.RequirePath(marker);
        if (outcome is not ("failed" or "succeeded" or "running")) return 2;
        await Console.OpenStandardOutput().WriteAsync(Encoding.UTF8.GetBytes("account-task:" + outcome + "\n"));
        File.WriteAllText(marker, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (outcome == "running") await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }
}
