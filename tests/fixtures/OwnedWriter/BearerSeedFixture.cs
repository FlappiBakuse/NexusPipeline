using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;

[SupportedOSPlatform("windows")]
internal static class BearerSeedFixture
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is not [var requested]) return 64;
        var owner = PhysicalPathPolicy.NormalizeWithoutLinks(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")
            ?? throw new IOException("fixture_owner_required"));
        var root = PhysicalPathPolicy.NormalizeWithoutLinks(requested);
        if (!root.StartsWith(owner.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("fixture_runtime_outside_owner");
        byte[] input = new byte[257]; char[] characters = new char[256];
        try
        {
            await using var stdin = Console.OpenStandardInput();
            var count = 0;
            while (count < input.Length)
            {
                var read = await stdin.ReadAsync(input.AsMemory(count));
                if (read == 0) break;
                count += read;
            }
            if (count != 64 || input.Take(count).Any(value => value is not (>= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f'))) return 65;
            var length = Encoding.ASCII.GetChars(input.AsSpan(0, count), characters);
            var contracts = new ContractReader();
            var instance = new InstanceRoot(contracts).OpenOrCreate(root);
            using var lease = InstanceLease.Acquire(instance, contracts);
            var roots = new PayloadApplyRoots(AppContext.BaseDirectory, Path.Combine(root, "extensions"));
            var database = File.Exists(Path.Combine(root, "state-initialization.guard"))
                ? StateDatabase.OpenExisting(instance, Path.Combine(root, "diagnostics", "controlled-bearer-seed"), new JournalStartupAdmission(instance, contracts, roots))
                : StateDatabase.CreateFresh(instance);
            var index = new StartupJournalRecovery(instance, contracts, database, roots).Recover(lease);
            var resources = new ResourceLeaseManager(new(index, TimeProvider.System));
            var secrets = new SecretFieldService(instance, database, lease, resources, index, contracts);
            var key = SecretFieldKey.HostControlBearer(instance.InstanceId);
            var current = await secrets.GetStateAsync(key, CancellationToken.None);
            using var value = new SecretValue(characters.AsSpan(0, length));
            var operation = Guid.NewGuid();
            var result = await secrets.ApplyAsync(operation, key, current.Revision, SecretDisposition.Set, value, CancellationToken.None);
            var addresses = NetworkInterface.GetAllNetworkInterfaces().Where(item => item.OperationalStatus == OperationalStatus.Up &&
                item.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .SelectMany(item => item.GetIPProperties().UnicastAddresses).Select(item => item.Address)
                .Where(item => item.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(item))
                .Select(item => item.ToString()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(32).ToArray();
            Console.WriteLine(JsonSerializer.Serialize(new { instanceId = instance.InstanceId, operationId = operation,
                revision = result.Revision, addresses }));
            return 0;
        }
        finally { CryptographicOperations.ZeroMemory(input); Array.Clear(characters); }
    }
}
