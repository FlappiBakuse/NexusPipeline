namespace NexusPipeline.Core.Extensions;

public enum ExtensionApplyAction { Install, Upgrade, Remove }
public enum ExtensionPackageKind { ManagedCode, DataSpecialized }
public enum ExtensionObservedActivity { None, Active, Unknown }

public sealed record ExtensionPackageIdentity(string ExtensionId, string PackageVersion, ExtensionPackageKind Kind,
    string ArchiveSha256, string ManifestSha256);
public sealed record ExtensionInstalledRevision(long Revision, bool IsInstalled, ExtensionPackageIdentity Package);
public sealed record ExtensionApplySelection(ExtensionApplyAction Action, string ExtensionId, long ExpectedInstalledRevision,
    ExtensionPackageIdentity? Candidate);
public sealed record ExtensionApplyPlan(ExtensionApplySelection Selection, ExtensionInstalledRevision? Original,
    bool DesiredEnabled, bool MustDeactivateExistingScope, bool MustActivateCandidate);

public sealed class ExtensionApplyException(string reasonCode) : Exception(reasonCode)
{
    public string ReasonCode { get; } = reasonCode;
}

public static class ExtensionApplyPolicy
{
    public static ExtensionApplyPlan Freeze(ExtensionApplySelection selection, ExtensionInstalledRevision? original,
        bool desiredEnabled, ExtensionObservedActivity activity, bool allowSameVersionRefresh = false)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ExtensionIdentifiers.Require(selection.ExtensionId);
        if (!Enum.IsDefined(selection.Action) || !Enum.IsDefined(activity) || selection.ExpectedInstalledRevision < 0 ||
            selection.ExpectedInstalledRevision > 9007199254740991) throw Invalid("extension_apply_selection_invalid");
        if (original is not null)
        {
            if (original.Revision is < 1 or > 9007199254740991) throw Invalid("extension_installed_revision_invalid");
            RequirePackage(original.Package, selection.ExtensionId);
        }
        if (selection.ExpectedInstalledRevision != (original?.Revision ?? 0))
            throw Invalid("extension_installed_revision_conflict");
        if (activity == ExtensionObservedActivity.Unknown) throw Invalid("extension_activity_unverified");
        switch (selection.Action)
        {
            case ExtensionApplyAction.Install:
                if (original?.IsInstalled == true) throw Invalid("extension_already_installed");
                RequirePackage(selection.Candidate, selection.ExtensionId);
                break;
            case ExtensionApplyAction.Upgrade:
                if (original?.IsInstalled != true) throw Invalid("extension_not_installed");
                RequirePackage(selection.Candidate, selection.ExtensionId);
                var candidate = selection.Candidate!;
                if (!allowSameVersionRefresh && candidate.PackageVersion == original.Package.PackageVersion &&
                    (candidate.ArchiveSha256 != original.Package.ArchiveSha256 ||
                        candidate.ManifestSha256 != original.Package.ManifestSha256))
                    throw Invalid("extension_same_version_content_changed");
                break;
            case ExtensionApplyAction.Remove:
                if (original?.IsInstalled != true) throw Invalid("extension_not_installed");
                if (selection.Candidate is not null) throw Invalid("extension_remove_candidate_forbidden");
                break;
        }
        return new(selection, original, desiredEnabled, activity == ExtensionObservedActivity.Active,
            selection.Action != ExtensionApplyAction.Remove && desiredEnabled);
    }

    private static void RequirePackage(ExtensionPackageIdentity? package, string extensionId)
    {
        if (package is null || package.ExtensionId != extensionId || !Enum.IsDefined(package.Kind) ||
            package.PackageVersion is not { Length: >= 1 and <= 80 } || package.PackageVersion.Any(char.IsControl) ||
            !Digest(package.ArchiveSha256) || !Digest(package.ManifestSha256))
            throw Invalid("extension_package_identity_invalid");
    }

    private static bool Digest(string value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static ExtensionApplyException Invalid(string reason) => new(reason);
}
