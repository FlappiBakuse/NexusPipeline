using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Installation;
using NexusPipeline.Platform.Storage.Maintenance;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal static class UpdateSourceSeed
{
    private const string FixtureKey = "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgWpZv4Vh8uPyfZ4ZZezQcI9hlF9tOwjVOs9kjl7OTziahRANCAATq7KMElrKXPlb7Tx9gEcy6kPu+eMQ6w04VAaUuSyNYx340Arzn7SuseSDzOi5ubrg82NS+sxXsk6FApFVR1YMG";
    private static readonly Guid Generation = Guid.Parse("23000000-0000-4000-8000-000000000001");

    internal static FrozenPayloadApply Bind(RuntimeInstance instance, StateDatabase database, FrozenPayloadApply plan, string candidate,
        bool executableTestPayload = false, HostUpdateTiming timing = HostUpdateTiming.Now,
        UpdateWindowIntent? window = null, string hostVersion = "0.18.0")
    {
        if (!File.Exists(Path.Combine(plan.TargetPath, ApplicationTree.ManifestPath))) ApplicationPayloadSeed.Write(plan.TargetPath);
        var manifest = ApplicationPayloadSeed.Write(candidate, hostVersion);
        var sourceDirectory = Path.Combine(Path.GetDirectoryName(candidate)!, "update-source-" + plan.OperationId.ToString("N"));
        Directory.CreateDirectory(sourceDirectory);
        var zipPath = Path.Combine(sourceDirectory, "payload.zip");
        ApplicationPayloadSeed.Archive(candidate, zipPath, manifest);
        return BindArchive(instance, database, plan, candidate, zipPath, manifest, StoreBinding(), hostVersion,
            executableTestPayload ? "test" : "production", timing,
            window ?? new(executableTestPayload ? UpdateWindowMode.Headless : UpdateWindowMode.Desktop, false, "/"));
    }

    internal static FrozenPayloadApply BindArchive(RuntimeInstance instance, StateDatabase database, FrozenPayloadApply plan,
        string candidate, string zipPath, byte[] manifest, object binding, string hostVersion, string profile,
        HostUpdateTiming timing, UpdateWindowIntent window)
    {
        var contracts = new ContractReader(); var blobs = new ImmutableBlobStore(instance, contracts);
        var zipBytes = File.ReadAllBytes(zipPath);
        var feed = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "nexus.update.feed", version = 1, channel = "stable", sequence = 1,
            releases = new[] { new { hostVersion, minReaderVersion = "0.18.0", releaseId = 1, assetId = 2,
                artifact = new { url = $"https://assets.example.test/releases/download/host/{hostVersion}/Host.zip", sizeBytes = zipBytes.Length, sha256 = Digest(zipBytes) },
                payloadManifestSha256 = Digest(manifest), publishedAt = "2026-10-22T00:00:00.0000000Z", storeBinding = binding } }
        });
        using var signer = ECDsa.Create(); signer.ImportPkcs8PrivateKey(Convert.FromBase64String(FixtureKey), out _);
        var signature = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "nexus.detached.signature", version = 1, keyId = "fixture-only", algorithm = "ecdsa-p256-sha256", encoding = "base64-p1363",
            signature = Convert.ToBase64String(signer.SignData(feed, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        });
        var verifier = Verifier(contracts, profile);
        var accepted = verifier.Verify(feed, signature, HostUpdateChannel.Stable);
        var saved = new SavedUpdateFeed(accepted.Feed.Identity, accepted.KeyId, blobs.PersistPlan(feed), blobs.PersistPlan(signature));
        var identity = new OperationIdentity(Guid.NewGuid(), "fixture-update-feed", UpdateFeedRows.RequestDigest(saved), "maintenance.feed.accept");
        database.Write((connection, transaction) =>
        {
            OperationRows.Accept(connection, transaction, identity, DateTimeOffset.UtcNow);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE operations SET status='completed' WHERE operation_id=$operation;";
            command.Parameters.AddWithValue("$operation", identity.OperationId.ToString("D"));
            if (command.ExecuteNonQuery() != 1) throw new IOException("fixture_feed_acceptance_failed");
            UpdateFeedRows.Accept(connection, transaction, saved, 1, identity.OperationId); return true;
        });
        var archive = blobs.PersistContent(zipPath, new("payload.zip", FileObjectKind.File, zipBytes.Length, Digest(zipBytes)), default);
        var source = new HostUpdateSource(UpdateSourceCodec.Format, 1, instance.InstanceId, plan.OperationId, plan.JournalId,
            saved, hostVersion, blobs.PersistPlan(manifest), archive, timing, window);
        var archiveTree = new FileTreeArchive(new(SnapshotBudget.Default), blobs);
        var before = archiveTree.CaptureApplication(plan.TargetPath, default);
        source = source with { Installation = new InstallationUpdateBinding(instance, contracts).Capture(plan.TargetPath, before, profile) };
        return plan with { Before = before,
            Candidate = archiveTree.CaptureApplication(candidate, default),
            MaintenanceSource = new UpdateSourceCodec(instance, contracts).Persist(source) };
    }

    internal static object StoreBinding() => new { channel = "stable", generationId = Generation, sequence = 1,
        catalogUrl = $"https://store.example.test/metadata/store/1/stable/generations/{Generation:D}/catalog.json",
        catalogSha256 = new string('c', 64),
        signatureUrl = $"https://store.example.test/metadata/store/1/stable/generations/{Generation:D}/catalog.sig.json" };
    internal static UpdateFeedVerifier Verifier(ContractReader contracts, string profile = "production")
    {
        using var signer = ECDsa.Create(); signer.ImportPkcs8PrivateKey(Convert.FromBase64String(FixtureKey), out _);
        return new(contracts, new(new Dictionary<string, byte[]> { ["fixture-only"] = signer.ExportSubjectPublicKeyInfo() },
            new("https://updates.example.test/metadata/update/1/"), new("https://store.example.test/metadata/store/1/"),
            [new("https://assets.example.test/releases/download/")], 1, 1, new(0, 18, 0), profile));
    }
    internal static byte[] TestSigningKey() => Convert.FromBase64String(FixtureKey);
    private static string Digest(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
}
