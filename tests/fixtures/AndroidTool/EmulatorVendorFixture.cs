using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NexusPipeline.AndroidToolFixture;

internal static class EmulatorVendorFixture
{
    internal static async Task<int?> RunAsync(string root, string[] args)
    {
        var path = Path.Combine(root, "fixture-vendor.json");
        if (!File.Exists(path)) return null;
        var input = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        var vendor = input["vendor"]!.GetValue<string>(); var nonce = input["nonce"]!.GetValue<string>();
        if ((args.SequenceEqual(new[] { "list2" }) || args.SequenceEqual(new[] { "list" })) && File.Exists(Path.Combine(root, "fixture-enumeration-delay")))
        {
            using var process = Process.GetCurrentProcess();
            File.WriteAllBytes(Path.Combine(root, "fixture-enumeration-wait.json"), JsonSerializer.SerializeToUtf8Bytes(new
                { processId = Environment.ProcessId, createdAt = process.StartTime.ToUniversalTime(), nonce }));
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
        if (args.SequenceEqual(new[] { "--fixture-vm", nonce })) return await WaitAsync(root, nonce);
        if (args.SequenceEqual(new[] { "list2" }) && vendor == "ldplayer")
        { Console.Write("0,Controlled VM,0,0,0,-1,-1\n"); return 0; }
        if (args.SequenceEqual(new[] { "list" }) && vendor == "nox")
        { Console.Write("nox,Controlled VM,0,0,0,-1\n"); return 0; }
        var launch = vendor switch
        {
            "ldplayer" => args.SequenceEqual(new[] { "launch", "--index", "0" }),
            "nox" => args.SequenceEqual(new[] { "launch", "-name:Controlled VM" }) || args.SequenceEqual(new[] { "launch", "-index:0" }),
            "bluestacks" => args.SequenceEqual(new[] { "--instance", "Pie64" }), _ => false
        };
        var quit = vendor switch
        {
            "ldplayer" => args.SequenceEqual(new[] { "quit", "--index", "0" }),
            "nox" => args.SequenceEqual(new[] { "quit", "-name:Controlled VM" }) || args.SequenceEqual(new[] { "quit", "-index:0" }), _ => false
        };
        if (!launch && !quit) return null;
        var devicePath = Path.Combine(root, "fixture-device.json");
        var device = JsonNode.Parse(File.ReadAllBytes(devicePath))!.AsObject(); device["online"] = launch;
        File.WriteAllBytes(devicePath + ".vendor", JsonSerializer.SerializeToUtf8Bytes(device)); File.Move(devicePath + ".vendor", devicePath, true);
        if (!launch) return 0;
        if (vendor == "bluestacks") return await WaitAsync(root, nonce);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--fixture-vm"); start.ArgumentList.Add(nonce);
        using var child = Process.Start(start) ?? throw new IOException("fixture_vm_start_failed");
        var wait = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(root, "fixture-vm.json")) && wait.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10);
        if (!File.Exists(Path.Combine(root, "fixture-vm.json"))) throw new IOException("fixture_vm_start_unconfirmed");
        return 0;
    }

    private static async Task<int> WaitAsync(string root, string nonce)
    {
        using var process = Process.GetCurrentProcess();
        File.WriteAllBytes(Path.Combine(root, "fixture-vm.json"), JsonSerializer.SerializeToUtf8Bytes(new
        { processId = Environment.ProcessId, createdAt = process.StartTime.ToUniversalTime(), nonce }));
        while (!File.Exists(Path.Combine(root, "fixture-vm-stop")))
        {
            try
            {
                var device = JsonNode.Parse(File.ReadAllBytes(Path.Combine(root, "fixture-device.json")))!;
                if (!device["online"]!.GetValue<bool>()) return 0;
            }
            catch (IOException) { }
            await Task.Delay(20);
        }
        return 0;
    }
}
