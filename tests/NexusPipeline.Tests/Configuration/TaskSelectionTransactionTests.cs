using System.Text.Json.Nodes;
using NexusPipeline.Modules.Configuration.Exchange;
using NexusPipeline.Modules.Configuration.Scripting;
using NexusPipeline.Modules.Plugins.Contracts;
using Xunit;

namespace NexusPipeline.Tests.Configuration;

public sealed class TaskSelectionTransactionTests
{
    [Theory]
    [InlineData("json")]
    [InlineData("yaml")]
    public void RestoresOriginalSelectionOverFinalBusinessCounters(string format)
    {
        WithFiles(format, (root, files) =>
        {
            var view = View(files, format);
            var transaction = TaskSelectionTransaction.Freeze(Path.Combine(root, "journal"), view, Fields(files));
            transaction.Apply(view, Patches(view, files, format));
            foreach (string file in files) File.WriteAllText(file, File.ReadAllText(file).Replace("7", "42"));
            transaction.Restore(); transaction.Complete();
            foreach (string file in files)
            {
                string text = File.ReadAllText(file); Assert.Contains("true", text); Assert.Contains("42", text);
            }
        });
    }

    [Theory]
    [InlineData("staged")]
    [InlineData("file:0")]
    [InlineData("file:1")]
    [InlineData("committed")]
    [InlineData("selected")]
    public void InterruptedMultiFileTransactionRecoversEveryFaultBoundary(string point)
    {
        WithFiles("json", (root, files) =>
        {
            var view = View(files, "json");
            string journal = Path.Combine(root, "journal");
            var transaction = TaskSelectionTransaction.Freeze(journal, view, Fields(files), phase =>
            { if (phase == point) throw new IOException("injected crash " + phase); });
            Assert.Throws<IOException>(() => transaction.Apply(view, Patches(view, files, "json")));
            var recovered = TaskSelectionTransaction.Load(journal, files.ToHashSet(StringComparer.OrdinalIgnoreCase));
            recovered.Restore(); recovered.Complete();
            foreach (string file in files) Assert.Equal("{\"enabled\":true,\"count\":7}", File.ReadAllText(file));
        });
    }

    [Fact]
    public void CasAndExternalSelectionConflictDoNotOverwriteExternalData()
    {
        WithFiles("json", (root, files) =>
        {
            var view = View(files, "json");
            var transaction = TaskSelectionTransaction.Freeze(Path.Combine(root, "journal"), view, Fields(files));
            var patches = Patches(view, files, "json");
            File.WriteAllText(files[0], "{\"enabled\":true,\"count\":99}");
            Assert.Throws<InvalidDataException>(() => transaction.Apply(view, patches));
            Assert.Contains("99", File.ReadAllText(files[0]));
            Assert.Contains("true", File.ReadAllText(files[1]));
        });
    }

    [Theory]
    [InlineData("counter")]
    [InlineData("unowned")]
    [InlineData("null")]
    [InlineData("unstaged")]
    public void CorruptJournalCannotOverwriteBusinessData(string corruption)
    {
        WithFiles("json", (root, files) =>
        {
            var view = View(files, "json");
            string directory = Path.Combine(root, "journal");
            var transaction = TaskSelectionTransaction.Freeze(directory, view, Fields(files), phase =>
            { if (phase == "staged") throw new IOException("crash"); });
            Assert.Throws<IOException>(() => transaction.Apply(view, Patches(view, files, "json")));
            string path = Path.Combine(directory, "journal.json");
            var journal = JsonNode.Parse(File.ReadAllText(path))!;
            if (corruption == "counter") journal["Pending"]![0]!["After"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"enabled\":false,\"count\":999}"));
            if (corruption == "unowned") journal["Fields"]!.AsArray().RemoveAt(0);
            if (corruption == "null") journal["Fields"] = null;
            if (corruption == "unstaged") journal["Pending"]!.AsArray().Clear();
            File.WriteAllText(path, journal.ToJsonString());
            Assert.Throws<InvalidDataException>(() => TaskSelectionTransaction.Load(directory, files.ToHashSet(StringComparer.OrdinalIgnoreCase)));
            foreach (string file in files) Assert.Equal("{\"enabled\":true,\"count\":7}", File.ReadAllText(file));
        });
    }

    private static TaskSelectionField[] Fields(string[] files) => files.Select((_, i) => new TaskSelectionField("config:" + i, new JsonArray("enabled"), "selection")).ToArray();
    private static TaskConfigView View(string[] files, string format)
    {
        var view = new TaskConfigView();
        for (int i = 0; i < files.Length; i++) view.AddConfig("config:" + i, files[i], format);
        return view;
    }
    private static TaskConfigPatch[] Patches(TaskConfigView view, string[] files, string format) => files.Select((_, i) =>
        new TaskConfigPatch("config:" + i, format, JsonNode.Parse(view.ReadConfig("config:" + i))!["revision"]!.GetValue<string>(),
            [new(new JsonArray("enabled"), JsonValue.Create(true), JsonValue.Create(false), "selection")])).ToArray();
    private static void WithFiles(string format, Action<string, string[]> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-task-txn-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string[] files = [Path.Combine(root, "one." + format), Path.Combine(root, "two." + format)];
            foreach (string file in files) File.WriteAllText(file, format == "json" ? "{\"enabled\":true,\"count\":7}" : "enabled: true\ncount: 7\n");
            action(root, files);
        }
        finally { Directory.Delete(root, true); }
    }
}
