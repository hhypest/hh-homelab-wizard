namespace HhHomelab.Wizard.Domain;

/// <summary>The installation this wizard session acts for: the local journal's installation identity
/// and the build-time profile. A remote marker must match both before contents count as own.</summary>
public sealed record InstallationOwner(Guid InstallationId, string ProfileFingerprint)
{
    public bool IsValid => InstallationId != Guid.Empty && ResourceIdentity.IsFingerprint(ProfileFingerprint);
}

/// <summary>Content of the remote ownership marker. No credentials, hosts, names or paths.</summary>
public sealed record OwnershipMarker(int Schema, Guid InstallationId, string ProfileFingerprint)
{
    public const int CurrentSchema = 1;

    public const string DirectoryName = ".hh-homelab-wizard";

    public const string FileName = "installation.json";

    public const int MaximumBytes = 4096;

    public bool IsValid => Schema == CurrentSchema
        && InstallationId != Guid.Empty
        && ResourceIdentity.IsFingerprint(ProfileFingerprint);
}

public enum OwnershipMarkerState
{
    Absent,
    Present,
    Invalid,
    Redirected,
    Unavailable,
}

/// <summary>Read-only observation of the marker below the configuration root. Invalid covers a
/// non-regular, oversized, unparsable or unknown-schema file; Redirected a symlink anywhere on the
/// marker path; Unavailable that the SSH user could not tell whether a marker exists.</summary>
public sealed record OwnershipObservation(OwnershipMarkerState State, OwnershipMarker? Marker = null)
{
    public bool IsValid => State switch
    {
        OwnershipMarkerState.Present => Marker is { IsValid: true },
        OwnershipMarkerState.Absent or OwnershipMarkerState.Invalid
            or OwnershipMarkerState.Redirected or OwnershipMarkerState.Unavailable => Marker is null,
        _ => false,
    };
}

/// <summary>Decides whether existing configuration contents belong to this installation. Only an
/// exact installation and profile match is own; a name, path or empty-looking directory is not
/// evidence. Profile changes and foreign or unreadable markers block instead of adopting.</summary>
public static class InstallationOwnershipAssessment
{
    public static PreflightCheck Evaluate(RemotePathSnapshot snapshot, InstallationOwner? expected)
    {
        if (expected is not null && !expected.IsValid)
            throw new ArgumentException("ownership.owner.invalid");
        var root = snapshot.Observation(RemotePathRole.ConfigurationRoot);
        var ownership = snapshot.Ownership;
        if (ownership.State == OwnershipMarkerState.Unavailable)
            return Blocking("ownership.marker-unavailable");
        if (ownership.State == OwnershipMarkerState.Redirected)
            return Blocking("ownership.marker-redirected");
        if (ownership.State == OwnershipMarkerState.Invalid)
            return Blocking("ownership.marker-invalid");
        // The owner comes from a journal; this build observes under its own profile. A journal of
        // another profile never continues here, whatever the marker says.
        if (expected is not null && expected.ProfileFingerprint != snapshot.ProfileFingerprint)
            return Blocking("ownership.profile-changed");
        if (ownership.State == OwnershipMarkerState.Present)
            return MarkerCheck(ownership.Marker!, expected);
        return UnmarkedCheck(root);
    }

    private static PreflightCheck MarkerCheck(OwnershipMarker marker, InstallationOwner? expected)
    {
        if (expected is null || marker.InstallationId != expected.InstallationId)
            return Blocking("ownership.foreign-installation");
        if (marker.ProfileFingerprint != expected.ProfileFingerprint)
            return Blocking("ownership.profile-changed");
        return new("ownership.own-installation", PreflightCheckStatus.Passed);
    }

    /// <summary>Without a marker only an absent or empty root is a new installation.</summary>
    private static PreflightCheck UnmarkedCheck(RemotePathObservation root)
    {
        if (root.Kind == RemotePathKind.Missing)
            return new("ownership.new-installation", PreflightCheckStatus.Passed);
        if (root.Kind != RemotePathKind.Directory)
            return Blocking("ownership.configuration-root-unverified");
        if (root.Contents == RemotePathContents.Empty)
            return new("ownership.new-installation", PreflightCheckStatus.Passed);
        if (root.Contents == RemotePathContents.NotEmpty)
            return Blocking("ownership.unmarked-contents");
        return Blocking("ownership.configuration-root-unverified");
    }

    private static PreflightCheck Blocking(string code) => new(code, PreflightCheckStatus.Blocking);
}
