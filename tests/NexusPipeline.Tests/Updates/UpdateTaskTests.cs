using NexusPipeline.Modules.Updates;
using NexusPipeline.Platform.Storage;
using Xunit;

namespace NexusPipeline.Tests.Updates;

public sealed class UpdateTaskTests
{
    [Theory]
    [InlineData("NexusPipeline.update.exe", true)]
    [InlineData("NexusPipeline Update.exe", false)]
    [InlineData("NexusPipeline.uninstall.exe", false)]
    [InlineData("unknown.exe", false)]
    public void FullPackageAdmitsOnlyTheCurrentUpdaterEntry(string entry, bool allowed)
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-package-entry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string zip = Path.Combine(root, "package.zip"), stage = Path.Combine(root, "staging");
            using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
                foreach (string name in new[] { "NexusPipeline.exe", entry })
                {
                    using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                    writer.Write("controlled package bytes");
                }
            string? error = UpdatePackage.Extract(zip, stage);
            Assert.Equal(allowed, error is null);
            if (allowed) Assert.Equal("controlled package bytes", File.ReadAllText(Path.Combine(stage, entry)));
            else Assert.False(Directory.Exists(stage));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ExpandedJournalPersistsTransportAndDerivesImmutableApplicationScopes()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-expanded-journal-" + Guid.NewGuid().ToString("N"));
        string staging = Path.Combine(root, "staging"), journal = Path.Combine(root, "task.json");
        Directory.CreateDirectory(staging);
        try
        {
            foreach (string name in new[] { "NexusPipeline.exe", UpdateRecipe.UpdateEntry, "README.md", ApplicationPayload.ManifestPath, ApplicationPayload.DesktopEntry })
            {
                string path = Path.Combine(staging, name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, name);
            }
            var assets = new[] { "Portable.zip", "Setup.exe", "Update.zip" }.Select((suffix, index) =>
                new UpdateReleaseAsset(index + 1, "NexusPipeline-v0.17.4-win-x64-" + suffix, "https://fixture.example/" + suffix, 20, new string('a', 64))).ToArray();
            var task = MakeTask("apply", "0.17.4", staging) with {
                JournalSchemaVersion = 3, PayloadSchemaVersion = 2,
                StagingInventory = UpdateInventory.Capture(staging), PreviousPayload = null,
                ReleaseEvidence = new(1, "v0.17.4", new string('b', 64), UpdateMigrationRegistry.NoStoreChangeRoute, assets) { Protocol = 2 },
                TransportReceipt = new("delta", assets[2], new string('c', 64), new string('d', 64), 20, 10),
            };
            task.Write(journal);
            var received = UpdateTask.Read(journal)!;
            Assert.Equal(task, received);
            Assert.Equal(5, received.ApplicationScopes.Length);
            Assert.Contains(UpdateRecipe.UpdateEntry, received.ApplicationScopes);
            Assert.Contains("resources/desktop", received.ApplicationScopes);
            Assert.Throws<InvalidDataException>(() => (received with { JournalSchemaVersion = 2 }).Write(journal));
            Assert.Throws<InvalidDataException>(() => (received with { TransportReceipt = received.TransportReceipt! with { RecipeSha256 = new string('e', 64) } }).Write(journal));
            Assert.Throws<InvalidDataException>(() => (received with { TransportReceipt = null }).Validate());
            Assert.Throws<InvalidDataException>(() => (received with { ReleaseEvidence = received.ReleaseEvidence! with { Protocol = 1 } }).Validate());
            Assert.Equal(task, UpdateTask.Read(journal));
            string retiredJournal = Path.Combine(root, "retired-task.json");
            var retired = task with { JournalSchemaVersion = 4, RemovedApplicationFiles = new([
                new ApplicationPayloadFile(ApplicationPayload.LegacyUpdateEntry, 17, new string('e',64), "update-entry")]) };
            retired.Write(retiredJournal);
            Assert.Equal(retired, UpdateTask.Read(retiredJournal));
            Assert.Contains(ApplicationPayload.LegacyUpdateEntry, retired.ApplicationScopes);
            Assert.Equal(6, retired.ApplicationScopes.Length);
            Assert.Throws<InvalidDataException>(() => (retired with { RemovedApplicationFiles = new([
                new ApplicationPayloadFile(ApplicationPayload.LegacyUpdateEntry, 18, new string('f',64), "update-entry")]) }).Write(retiredJournal));
            Assert.Equal(retired, UpdateTask.Read(retiredJournal));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void FailureResultRequiresTheTransactionAndPreservedDesktopIntent()
    {
        var task = MakeTask("apply", "0.17.1", Path.GetTempPath()) with
        {
            TransactionId = Guid.NewGuid().ToString("N"),
        };
        var result = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(new
        {
            task.TransactionId, task.Version, Succeeded = false, Code = "policy-unavailable",
            AtUtc = DateTimeOffset.UtcNow, task.DesktopResumeIntent,
        }))!.AsObject();
        Assert.True(UpdateApply.IsFailedTransactionResult(task, result.ToJsonString()));
        result["Succeeded"] = true;
        Assert.False(UpdateApply.IsFailedTransactionResult(task, result.ToJsonString()));
        result["Succeeded"] = false;
        foreach (string field in new[] { "TransactionId", "Version", "DesktopResumeIntent", "AtUtc" })
        {
            var changed = result.DeepClone().AsObject();
            changed[field] = field switch
            {
                "TransactionId" => System.Text.Json.Nodes.JsonValue.Create(Guid.NewGuid().ToString("N")),
                "Version" => System.Text.Json.Nodes.JsonValue.Create("0.17.2"),
                "AtUtc" => System.Text.Json.Nodes.JsonValue.Create(task.CreatedAt!.Value.AddSeconds(-1)),
                _ => System.Text.Json.Nodes.JsonNode.Parse("{\"SchemaVersion\":1,\"Mode\":\"show\"}"),
            };
            Assert.Throws<InvalidDataException>(() => UpdateApply.IsFailedTransactionResult(task, changed.ToJsonString()));
        }
        result.Remove("DesktopResumeIntent");
        Assert.Throws<InvalidDataException>(() => UpdateApply.IsFailedTransactionResult(task, result.ToJsonString()));
    }

    [Fact]
    public void DesktopIntentIsStrictImmutableAndIndependentOfRestartHandoff()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-update-intent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "task.json");
        try
        {
            foreach (bool visible in new[] { false, true })
            foreach (bool handoff in new[] { false, true })
            foreach (bool web in new[] { false, true })
            {
                var task = MakeTask("apply", "0.17.0", Path.Combine(root, "staging")) with
                {
                    TransactionId = Guid.NewGuid().ToString("N"), TargetImageHash = new('a', 64),
                    DesktopResumeIntent = DesktopResumeIntent.FromVisible(visible),
                    RestartHandoffId = handoff ? Guid.NewGuid().ToString("N") : null,
                };
                task.Write(file);
                var received = UpdateTask.Read(file)!;
                Assert.Equal(task.DesktopResumeIntent, received.DesktopResumeIntent);
                string[] arguments = UpdateApply.BuildServiceArguments(received, web);
                Assert.Equal(visible ? "show" : "background", arguments[Array.IndexOf(arguments, "--desktop-intent") + 1]);
                Assert.Equal(web, arguments.Contains("--web"));
                Assert.Equal(handoff, arguments.Contains("--handoff"));
                Assert.Throws<InvalidDataException>(() => (task with { DesktopResumeIntent = DesktopResumeIntent.FromVisible(!visible) }).Write(file));
                string original = File.ReadAllText(file);
                foreach (var invalid in new DesktopResumeIntent?[] { null, new(2, "show"), new(1, "restore"), new(1, "SHOW") })
                {
                    File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(task with { DesktopResumeIntent = invalid }));
                    Assert.Equal(UpdateFileState.Unsupported, UpdateTask.ReadState(file).State);
                }
                File.WriteAllText(file, original);
                task.Clear(file);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static UpdateTask MakeTask(string mode, string version, string staging) => UpdateTask.Create(mode, version, staging) with
    {
        TargetPayload = new(new string('a', 64), new string('b', 64), new string('c', 64), "g0170"),
        PreviousPayload = new(new string('d', 64), new string('e', 64), new string('f', 64), "g0170"),
        PackageSha256 = new string('1', 64), DesktopStopped = true,
        StagingInventory = new([]), BackupInventory = new([]), WorkerSha256 = new string('2', 64),
        DesktopResumeIntent = mode == "defer" ? null : DesktopResumeIntent.FromVisible(false),
    };

    [Fact]
    public void UnsupportedJournalAndMarkerRemainDistinctFromMissingAndCannotBeRewritten()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-update-format-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "task.json");
        var task = MakeTask("apply", "0.16.15", Path.Combine(root, "staging"));
        try
        {
            Assert.Equal(UpdateFileState.Missing, UpdateTask.ReadState(file).State);
            string valid = System.Text.Json.JsonSerializer.Serialize(task);
            foreach (string invalid in new[]
            {
                "{}", "{broken", System.Text.Json.JsonSerializer.Serialize(task with { TargetPayload = null }),
                System.Text.Json.JsonSerializer.Serialize(task with { PackageSha256 = null }),
                System.Text.Json.JsonSerializer.Serialize(task with { PayloadSchemaVersion = 2 }),
                System.Text.Json.JsonSerializer.Serialize(task with { SwappedAssetCount = 1 }), valid.Replace("\"CreatedAt\":", "\"createdAt\":"),
                valid.Replace("\"Phase\":\"ApplyRequested\"", "\"Phase\":\"future\""),
                valid.Replace("\"Mode\":\"apply\"", "\"Mode\":\"apply\",\"mode\":\"apply\""),
                System.Text.Json.JsonSerializer.Serialize(task with { CreatedAt = null }),
                System.Text.Json.JsonSerializer.Serialize(task with { Phase = UpdatePhase.AwaitingStartup }),
            })
            {
                File.WriteAllText(file, invalid);
                Assert.Equal(UpdateFileState.Unsupported, UpdateTask.ReadState(file).State);
                Assert.Throws<InvalidDataException>(() => task.Write(file));
                Assert.Throws<InvalidDataException>(() => task.Clear(file));
                Assert.Equal(invalid, File.ReadAllText(file));
            }
            File.WriteAllText(file, valid);
            Assert.Throws<InvalidDataException>(() => (task with { CreatedAt = task.CreatedAt!.Value.AddSeconds(1) }).Write(file));
            Assert.Equal(valid, File.ReadAllText(file));
            string marker = Path.Combine(root, "marker");
            Assert.Equal(UpdateFileState.Missing, UpdateApply.ReadVersionState(marker).State);
            foreach (string invalid in new[] { "", "unknown", "0.16.15\n0.16.14" })
            {
                File.WriteAllText(marker, invalid);
                Assert.Equal(UpdateFileState.Unsupported, UpdateApply.ReadVersionState(marker).State);
                Assert.Equal(invalid, File.ReadAllText(marker));
            }
            File.WriteAllText(marker, "0.16.15\n");
            Assert.Equal(UpdateFileState.Current, UpdateApply.ReadVersionState(marker).State);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void CommittedCleanupKeepsJournalUntilEveryOwnedResourceIsRemoved()
    {
        foreach (string packageSource in new[] { "installer", "download" })
        foreach (string interruption in new[] { "staging", "downloads", "backup", "worker", "containers", "marker", "journal" })
        {
            string root = Path.Combine(Path.GetTempPath(), "nxp-update-cleanup-" + Guid.NewGuid().ToString("N"));
            string update = Path.Combine(root, ".nxp-update"), staging = Path.Combine(update, "staging", "0.16.15.g1");
            string backup = Path.Combine(root, ".nxp-backup", "previous");
            Directory.CreateDirectory(staging); Directory.CreateDirectory(backup);
            string workers = Path.Combine(root, ".nxp", "runtime", "workers");
            Directory.CreateDirectory(workers);
            string worker = Path.Combine(workers, "update-" + Guid.NewGuid().ToString("N") + ".exe");
            File.WriteAllText(worker, "worker");
            File.WriteAllText(Path.Combine(staging, "README.md"), "current");
            File.WriteAllText(Path.Combine(backup, "NexusPipeline.exe"), "backup");
            JsonUtil.WriteAtomic(Path.Combine(root, ".nxp-version"), "0.16.15" + Environment.NewLine);
            string journal = Path.Combine(update, "task.json");
            string package = Path.Combine(update, "NexusPipeline-v0.16.15-win-x64.zip.g1");
            string checksum = Path.Combine(update, "NexusPipeline-v0.16.15-win-x64.zip.sha256.g1");
            if (packageSource == "download")
            {
                File.WriteAllText(package, "downloaded archive");
                File.WriteAllText(checksum, UpdateApply.ImageHash(package));
            }
            var task = MakeTask("apply", "0.16.15", staging) with
            {
                Mode = "completed", Phase = UpdatePhase.Committed, SwappedAssetCount = 4, TransactionId = Guid.NewGuid().ToString("N"),
                TargetImageHash = new string('a', 64), WorkerIdentity = new(42, DateTime.UtcNow, worker),
                WorkerSha256 = UpdateApply.ImageHash(worker), StagingInventory = UpdateInventory.Capture(staging), BackupInventory = UpdateInventory.Capture(backup),
                PackageSource = packageSource,
                PackageSha256 = packageSource == "download" ? UpdateApply.ImageHash(package) : new string('1', 64),
                PackageChecksumSha256 = packageSource == "download" ? UpdateApply.ImageHash(checksum) : null,
            };
            task.Write(journal);
            try
            {
                Assert.Throws<IOException>(() => UpdateCleanup.Complete(task, root, _ => false,
                    step => { if (step == interruption) throw new IOException("injected interruption"); }));
                if (interruption != "journal")
                {
                    Assert.Equal(task, UpdateTask.Read(journal));
                    UpdateCleanup.Complete(task, root, _ => false);
                }
                Assert.False(File.Exists(journal));
                Assert.False(File.Exists(worker));
                Assert.False(File.Exists(package));
                Assert.False(File.Exists(checksum));
                Assert.False(Directory.Exists(staging));
                Assert.False(Directory.Exists(backup));
                Assert.False(File.Exists(Path.Combine(root, ".nxp-version")));
            }
            finally { Directory.Delete(root, true); }
        }
    }

    [Fact]
    public void CleanupRejectsUnknownNestedOrChangedFilesBeforeDeletingAnyRecoveryProof()
    {
        foreach (string conflict in new[] { "staging", "backup", "worker", "package", "checksum", "changed-staging" })
        {
            string root = Path.Combine(Path.GetTempPath(), "nxp-update-owner-" + Guid.NewGuid().ToString("N"));
            string update = Path.Combine(root, ".nxp-update"), stage = Path.Combine(update, "staging", "0.17.0.g1");
            string backup = Path.Combine(root, ".nxp-backup", "previous");
            Directory.CreateDirectory(Path.Combine(stage, "resources")); Directory.CreateDirectory(Path.Combine(backup, "resources"));
            string workers = Path.Combine(root, ".nxp", "runtime", "workers"); Directory.CreateDirectory(workers);
            string worker = Path.Combine(workers, "update-" + Guid.NewGuid().ToString("N") + ".exe");
            string package = Path.Combine(update, "NexusPipeline-v0.17.0-win-x64.zip.g1"), checksum = Path.Combine(update, "NexusPipeline-v0.17.0-win-x64.zip.sha256.g1");
            string stageFile = Path.Combine(stage, "resources", "known.bin"), backupFile = Path.Combine(backup, "resources", "known.bin");
            File.WriteAllText(stageFile, "candidate"); File.WriteAllText(backupFile, "previous");
            File.WriteAllText(worker, "worker"); File.WriteAllText(package, "package"); File.WriteAllText(checksum, "checksum");
            string journal = Path.Combine(update, "task.json");
            var task = MakeTask("apply", "0.17.0", stage) with
            {
                Mode = "completed", Phase = UpdatePhase.Committed, SwappedAssetCount = 4, TransactionId = Guid.NewGuid().ToString("N"),
                TargetImageHash = new string('a', 64), WorkerIdentity = new(42, DateTime.UtcNow, worker), WorkerSha256 = UpdateApply.ImageHash(worker),
                PackageSha256 = UpdateApply.ImageHash(package), PackageChecksumSha256 = UpdateApply.ImageHash(checksum),
                StagingInventory = UpdateInventory.Capture(stage), BackupInventory = UpdateInventory.Capture(backup),
            };
            task.Write(journal);
            string protectedPath = conflict switch
            {
                "staging" => Path.Combine(stage, "resources", "unknown.bin"), "backup" => Path.Combine(backup, "resources", "unknown.bin"),
                "worker" => worker, "package" => package, "checksum" => checksum, _ => stageFile,
            };
            File.WriteAllText(protectedPath, "private bytes");
            try
            {
                Assert.Throws<IOException>(() => UpdateCleanup.Complete(task, root, _ => false));
                Assert.Equal("private bytes", File.ReadAllText(protectedPath));
                Assert.Equal("previous", File.ReadAllText(backupFile));
                Assert.True(File.Exists(stageFile)); Assert.Equal(task, UpdateTask.Read(journal));
            }
            finally { Directory.Delete(root, true); }
        }
    }

    [Fact]
    public void InventoryDeletionRechecksBytesAndPreservesUnknownDirectories()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-inventory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "resources"));
        string file = Path.Combine(root, "resources", "known.bin"); File.WriteAllText(file, "owned");
        var inventory = UpdateInventory.Capture(root);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "resources", "unknown"));
            Assert.Throws<IOException>(() => inventory.DeleteOwned(root, () => { })); Assert.True(File.Exists(file));
            Directory.Delete(Path.Combine(root, "resources", "unknown"));
            Assert.Throws<IOException>(() => inventory.DeleteOwned(root, () => File.WriteAllText(file, "changed")));
            Assert.Equal("changed", File.ReadAllText(file));
            File.WriteAllText(file, "owned"); inventory.DeleteOwned(root, () => { });
            inventory.DeleteOwned(root, () => { }); Assert.False(Directory.Exists(Path.Combine(root, "resources")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void CreatedDownloadCleanupPreservesUnknownAndChangedBytesAndRejectsExistingStaging()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-created-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string stage = Path.Combine(root, "staging"), file = Path.Combine(stage, new string('a', 100), new string('b', 100), "resources", "known.bin");
        var artifacts = new UpdateCreatedArtifacts(root);
        artifacts.EnsureDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "created");
        artifacts.FileCreated(file, new FileInfo(file).Length, UpdateApply.ImageHash(file));
        string zip = Path.Combine(root, "package.zip");
        File.WriteAllText(zip, "download");
        artifacts.FileCreated(zip, new FileInfo(zip).Length, UpdateApply.ImageHash(zip));
        try
        {
            string unknown = Path.Combine(Path.GetDirectoryName(file)!, "user-empty");
            Directory.CreateDirectory(unknown);
            Assert.Throws<IOException>(() => artifacts.Cleanup());
            Assert.True(File.Exists(zip)); Assert.Equal("created", File.ReadAllText(file));
            Assert.NotNull(UpdatePackage.Extract(zip, stage));
            Assert.True(Directory.Exists(unknown));
            Directory.Delete(unknown);
            File.WriteAllText(zip, "changed");
            Assert.Throws<IOException>(() => artifacts.Cleanup());
            Assert.Equal("changed", File.ReadAllText(zip)); Assert.Equal("created", File.ReadAllText(file));
            File.WriteAllText(zip, "download");
            artifacts.Cleanup(); artifacts.Cleanup();
            Assert.False(File.Exists(zip)); Assert.False(Directory.Exists(stage));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void InterruptedRollbackPreservationReusesOnlyItsExactReceiptAndCompleteFields()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-preservation-" + Guid.NewGuid().ToString("N"));
        string stage = Path.Combine(root, ".nxp-update", "staging", "0.17.0.g1");
        Directory.CreateDirectory(stage);
        string source = Path.Combine(stage, "README.md"), journal = Path.Combine(root, ".nxp-update", "task.json");
        File.WriteAllText(source, "candidate");
        var task = MakeTask("apply", "0.17.0", stage) with
        {
            Phase = UpdatePhase.RollbackPending, TransactionId = Guid.NewGuid().ToString("N"), TargetImageHash = new('a', 64),
            WorkerIdentity = new(42, DateTime.UtcNow, Path.Combine(root, "worker.exe")),
            StagingInventory = UpdateInventory.Capture(stage),
        };
        task.Write(journal);
        try
        {
            File.WriteAllText(source, "changed");
            var preserved = UpdateRollbackProtection.Prepare(task, root, journal, []);
            string directory = Path.Combine(root, ".nxp", "state", "updates", task.TransactionId + ".preserved");
            string receipt = directory + ".json", retained = Path.Combine(directory, "staging", "README.md");
            byte[] original = File.ReadAllBytes(receipt);
            Assert.False(File.Exists(source));
            Assert.Equal("changed", File.ReadAllText(retained));
            Assert.Equal(preserved, UpdateRollbackProtection.Prepare(preserved, root, journal, []));
            Assert.Equal(original, File.ReadAllBytes(receipt));
            var malformed = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(journal))!;
            malformed["Preservation"]!["Files"]![0]!["File"]!.AsObject().Remove("IsDirectory");
            File.WriteAllText(journal, malformed.ToJsonString());
            Assert.Equal(UpdateFileState.Unsupported, UpdateTask.ReadState(journal).State);
            File.Delete(journal); preserved.Write(journal);
            var unknown = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(receipt))!;
            unknown["user"] = "retain";
            File.WriteAllText(receipt, unknown.ToJsonString());
            byte[] changed = File.ReadAllBytes(receipt);
            Assert.Throws<IOException>(() => UpdateRollbackProtection.Prepare(preserved, root, journal, []));
            Assert.Equal(changed, File.ReadAllBytes(receipt));
            Assert.Equal("changed", File.ReadAllText(retained));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RestartHandoffPersistsThroughWorkerJournalPhases()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-update-journal-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(root, "task.json");
        try
        {
            string handoff = Guid.NewGuid().ToString("N");
            var task = MakeTask("apply", "0.16.15", Path.Combine(root, "staging")) with
            {
                RestartHandoffId = handoff,
            };
            task.Write(file);
            UpdateTask received = Assert.IsType<UpdateTask>(UpdateTask.Read(file));
            Assert.Equal(handoff, received.RestartHandoffId);

            received = received with { Phase = UpdatePhase.AwaitingStartup, SwappedAssetCount = 4, TransactionId = Guid.NewGuid().ToString("N"),
                TargetImageHash = new string('a', 64), WorkerIdentity = new(42, DateTime.UtcNow, Path.Combine(root, "worker.exe")) };
            received.Write(file);
            Assert.Equal(handoff, UpdateTask.Read(file)?.RestartHandoffId);
            Assert.Equal(task.CreatedAt, UpdateTask.Read(file)?.CreatedAt);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InvalidRestartHandoffIsNotAdopted()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-update-journal-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(root, "task.json");
        try
        {
            var task = MakeTask("apply", "0.16.15", Path.Combine(root, "staging")) with
            {
                RestartHandoffId = "not-an-id",
            };
            Assert.Throws<InvalidDataException>(() => task.Write(file));
            Directory.CreateDirectory(root);
            string bytes = System.Text.Json.JsonSerializer.Serialize(task);
            File.WriteAllText(file, bytes);
            Assert.Equal(UpdateFileState.Unsupported, UpdateTask.ReadState(file).State);
            Assert.Throws<InvalidDataException>(() => UpdateTask.Read(file));
            Assert.Equal(bytes, File.ReadAllText(file));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
