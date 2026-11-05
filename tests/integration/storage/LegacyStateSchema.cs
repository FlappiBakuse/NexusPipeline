using System.Security.Cryptography;
using System.Text;

namespace NexusPipeline.StorageTests;

internal static class LegacyStateSchema
{
    internal const string DataDigest = "8c7f8b5a067849ee4f352bcd7cc1a236b24d891d3aec09d53b5b561a19fca25e";
    internal static string DataSql => Read("Data", DataDigest);
    internal const string DocumentDigest = "bda5d5ce2f37fa3a45bf545bec7788d6033b1d9f0af6b4167f8798d42c891797";
    internal static string DocumentSql => Read("Document", DocumentDigest);
    internal const string ImageDigest = "b61015aebc62536e95f8ed898d93bc76c57191dbc7b7c1ac7468b630d0953668";
    internal static string ImageSql => Read("Image", ImageDigest);
    internal const string SecretDigest = "d66cd70d4f939eb81e9f1a1ccf20f9fc148f36b3db868bb8a0ba6ed4a7c13ab8";
    internal static string SecretSql => Read("Secret", SecretDigest);
    internal const string ManualDigest = "fe0677be1e700ae03cd69dd8ad7a01635c4c3fc0850c828f0edfa86a2e8c8ca5";
    internal static string ManualSql => Read("Manual", ManualDigest);
    internal const string EffectDigest = "a0f4c9dbaaacfa9e6e5045b5065c01b49364616f279f275e125224851bdde2e3";
    internal static string EffectSql => Read("Effect", EffectDigest);
    internal const string RotationDigest = "35363411140e460a298605038f63d3bbbb7679d6d80ac807c3109ebcac69e240";
    internal static string RotationSql => Read("Rotation", RotationDigest);
    internal const string NotificationDigest = "d11f545eece4478963fbe7fc3a4dcb45ac846a0708e3d5566fcdb17b82859c76";
    internal static string NotificationSql => Read("Notification", NotificationDigest);
    internal const string EpochDigest = "c414f85a10dfb9dd9e4ab9930d25a7d38a549a77268dab7c1afa517fc0537cd8";
    internal static string EpochSql => Read("Epoch", EpochDigest);
    internal const string SettingsDigest = "032e18a0fa31b7ccf3a2e628814f160c0262b72e0bbbd8a462095577bba5fc88";
    internal static string SettingsSql => Read("Settings", SettingsDigest);
    internal const string AutomationDigest = "6bdbb72b6a873d25cbe2d4172b6b187c2337f44f25112ac708cbd7e77b39c85d";
    internal static string AutomationSql => Read("Automation", AutomationDigest);
    internal const string PriorDigest = "3daf2ab3f782c5e7d3155bc6417cbaf5ac102df08168bb79b72d19838f0f4aa9";
    internal static string PriorSql => Read("Prior", PriorDigest);
    internal const string PreviousDigest = "1c6e5a7070282b3f69d1ca7daf69dea4a18852a8c93f04f37de4e8c1a63a4378";
    internal static string PreviousSql => Read("Previous", PreviousDigest);

    private static string Read(string name, string digest)
    {
        using var stream = typeof(LegacyStateSchema).Assembly.GetManifestResourceStream("LegacyState." + name + ".sql")
            ?? throw new IOException("legacy_schema_fixture_missing");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        var sql = reader.ReadToEnd();
        if (Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql))) != digest)
            throw new IOException("legacy_schema_fixture_changed");
        return sql;
    }
}
