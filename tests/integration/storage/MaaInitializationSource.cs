using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO.Compression;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Execution.Judgement;

namespace NexusPipeline.StorageTests;

internal static class MaaInitializationSource
{
    internal static void Prepare(ProjectSourceFixture project, string package, string marker, bool fail, bool cancel)
    {
        PrepareProgram(project, package);
        var declaration = Path.Combine(project.Path, "assets/interface.json");
        var pi = JsonNode.Parse(File.ReadAllBytes(declaration))!.AsObject();
        pi["pretask"] = new JsonObject { ["name"] = "prepare", ["exec"] = "../init/run.exe",
            ["args"] = new JsonArray("initialization-fixture", marker, cancel ? "cancel" : fail ? "fail" : "success") };
        File.WriteAllBytes(declaration, JsonSerializer.SerializeToUtf8Bytes(pi));
        var config = JsonNode.Parse(File.ReadAllBytes(project.ConfigurationFile!))!.AsObject();
        config["instances"]![0]!["tasks"]!.AsArray().Insert(0, new JsonObject {
            ["id"] = "prepare-one", ["taskName"] = "__MXU_PRETASK__prepare", ["enabled"] = true,
            ["enabledByController"] = new JsonObject { ["PC"] = true }, ["optionValues"] = new JsonObject() });
        File.WriteAllBytes(project.ConfigurationFile!, JsonSerializer.SerializeToUtf8Bytes(config));
        var catalog = project.Catalog; var script = new AutomationDefinitionStore(catalog.Database).Read().Scripts[0];
        var tree = new FileTreeArchive(new(SnapshotBudget.Default), new(catalog.Instance, new())).Capture(project.ConfigurationFile!, default).Snapshot;
        project.Save(new(project.Address, 2, 1, AdapterConfigurationPolicy.Contract(script, script.AdapterResources[0]), tree, ConfigurationUpdateKind.Manual));
    }

    internal static void PrepareProgram(ProjectSourceFixture project, string package, bool agent = false)
    {
        var native = Path.Combine(project.Path, "native"); Directory.CreateDirectory(native);
        using var archive = ZipFile.OpenRead(package);
        var prefix = agent ? "bin/" : "runtime/win-x64/";
        var names = agent ? archive.Entries.Where(entry => entry.FullName.StartsWith(prefix, StringComparison.Ordinal) &&
            entry.FullName == prefix + entry.Name && entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).Select(entry => entry.Name).ToArray() :
            ["MaaFramework.dll", "MaaCustomControlUnit.dll"];
        foreach (var name in names)
        {
            using var input = (archive.GetEntry(prefix + name) ?? throw new IOException("fixture_native_missing")).Open();
            using var output = new FileStream(Path.Combine(native, name), FileMode.CreateNew, FileAccess.Write);
            input.CopyTo(output);
        }
        var program = Path.Combine(project.Path, "init"); Directory.CreateDirectory(program);
        CopyProgram(program);
    }

    internal static void CopyProgram(string program)
    {
        var original = JudgementInterpreterLease.Inspect(Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!);
        File.Copy(original.Path, Path.Combine(program, "run.exe"), true);
        foreach (var item in original.RuntimeFiles)
        {
            var path = Path.Combine(program, item.RelativePath); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(Path.Combine(Path.GetDirectoryName(original.Path)!, item.RelativePath), path, true);
        }
    }

    internal static async Task InterruptWhenStarted(string marker, Task<AttemptReceipt?> running, CancellationTokenSource stopping)
    {
        try { await WaitForStart(marker, running); }
        finally { stopping.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(90)); }
    }

    private static async Task WaitForStart(string marker, Task<AttemptReceipt?> running)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        while (!File.Exists(marker))
        {
            if (running.IsCompleted) throw new IOException("fixture_initialization_not_started");
            await Task.Delay(20, deadline.Token);
        }
    }

    internal static async Task CancelWhenStarted(ExtensionCatalogFixture catalog, Guid journal, string marker, Task<AttemptReceipt?> running)
    {
        try
        {
            await WaitForStart(marker, running);
        }
        finally
        {
            if (!running.IsCompleted)
            {
                var state = catalog.Lease.PublishExecution(() => AttemptWriterState.Read(catalog.Instance, catalog.Database, new(), journal));
                new ExecutionCancellationStore(catalog.Instance, catalog.Database, new(), catalog.Lease).Cancel(
                    ExecutionCancellationStore.Identity(Guid.NewGuid(), "fixture", journal, state.Journal.OperationId,
                        Guid.Parse(state.Attempt.RunId), Guid.Parse(state.Attempt.AttemptId)), journal, DateTimeOffset.UtcNow);
            }
            await running.WaitAsync(TimeSpan.FromSeconds(45));
        }
    }
}
