using System.Text.Json;
using NexusPipeline.Modules.Updates;

internal static class MigrationCut
{
    private static int _cut;
    internal static async Task<int> RunAsync(int cut)
    {
        if (cut is < 1 or > 9 || !File.Exists(".nxp-test-fixture")
            || File.ReadAllText(".nxp-test-fixture") != "owned-process-contract") return 4;
        _cut = cut;
        int checkpoint = 0;
        string root = Environment.CurrentDirectory;
        await UpdateDataMigration.PrepareAsync(root, Path.Combine(root, "transaction"), [(new Owner(), "2")],
            new([new Converter()]), default, state =>
            {
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(state);
                using (var file = new FileStream("journal.tmp", FileMode.Create, FileAccess.Write, FileShare.None))
                { file.Write(bytes); file.Flush(true); }
                File.Move("journal.tmp", "journal.json", true);
                if (++checkpoint == cut)
                {
                    File.WriteAllText("cut-ready", checkpoint.ToString());
                    Thread.Sleep(Timeout.Infinite);
                }
            });
        return 0;
    }
    private sealed class Owner : IUpdateStoreOwner
    {
        public string Id => "fixture";
        public string ActiveRoot(string installation) => Path.Combine(installation, "active");
        public string DescribeSchema(string root) => "1";
        public void Validate(string root, string schema)
        {
            if (!File.Exists(Path.Combine(root, "data.txt")) || schema is not ("1" or "2")) throw new InvalidDataException();
        }
    }
    private sealed class Converter : IUpdateDataMigrator
    {
        public string Id => "fixture-convert";
        public string StoreId => "fixture";
        public string InputSchema => "1";
        public string OutputSchema => "2";
        public Task ConvertAsync(string input, string output, CancellationToken token)
        {
            File.WriteAllText(Path.Combine(output, "data.txt"), File.ReadAllText(Path.Combine(input, "data.txt")) + "converted");
            if (_cut == 9) { File.WriteAllText("cut-ready", "9"); Thread.Sleep(Timeout.Infinite); }
            return Task.CompletedTask;
        }
    }
}
