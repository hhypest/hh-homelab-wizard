namespace HhHomelab.Wizard.Domain;

/// <summary>Host-side directories of the fixed v1 media layout. Container paths match the
/// qBittorrent/Arr/Jellyfin contracts; the configuration root has no container mapping.</summary>
public enum RemotePathRole
{
    DataRoot,
    Torrents,
    TorrentsMovies,
    TorrentsSeries,
    Media,
    MediaMovies,
    MediaSeries,
    ConfigurationRoot,
}

public sealed record RemotePathEntry(RemotePathRole Role, string HostPath, string? ContainerPath);

/// <summary>Validated user choice of the shared data root and the configuration root. Validation is
/// lexical only; symlinks, volumes, owners and contents are observed separately.</summary>
public sealed class MediaPathLayout
{
    public const int MaximumPathLength = 240;

    /// <summary>Keeps every ancestor depth inside the observation contract.</summary>
    public const int MaximumComponents = 32;

    private static readonly (RemotePathRole Role, string Relative)[] DataDirectories =
    [
        (RemotePathRole.DataRoot, ""),
        (RemotePathRole.Torrents, "torrents"),
        (RemotePathRole.TorrentsMovies, "torrents/movies"),
        (RemotePathRole.TorrentsSeries, "torrents/tv"),
        (RemotePathRole.Media, "media"),
        (RemotePathRole.MediaMovies, "media/movies"),
        (RemotePathRole.MediaSeries, "media/tv"),
    ];

    public string DataRoot { get; }

    public string ConfigurationRoot { get; }

    public IReadOnlyList<RemotePathEntry> Entries { get; }

    public MediaPathLayout(string dataRoot, string configurationRoot)
    {
        if (!IsVolumePath(dataRoot))
            throw new ArgumentException("paths.data-root.invalid");
        if (!IsVolumePath(configurationRoot))
            throw new ArgumentException("paths.configuration-root.invalid");
        if (Overlaps(dataRoot, configurationRoot))
            throw new ArgumentException("paths.roots.overlap");

        DataRoot = dataRoot;
        ConfigurationRoot = configurationRoot;
        var entries = DataDirectories.Select(directory => new RemotePathEntry(directory.Role,
            Combine(dataRoot, directory.Relative), Combine("/data", directory.Relative))).ToList();
        entries.Add(new(RemotePathRole.ConfigurationRoot, configurationRoot, null));
        Entries = entries.AsReadOnly();
    }

    /// <summary>Absolute normalized path below a DSM volume mount: no empty, ".", ".." or control
    /// components, no trailing slash. Spaces and non-ASCII names are allowed.</summary>
    public static bool IsVolumePath(string? path)
    {
        if (path is not { Length: > 0 and <= MaximumPathLength } || path[0] != '/')
            return false;
        var components = path[1..].Split('/');
        if (components.Length is < 2 or > MaximumComponents)
            return false;
        if (!PreflightVolume.IsVolumeMount("/" + components[0]))
            return false;
        return components.All(ValidComponent);
    }

    public static string VolumeOf(string path) => "/" + path[1..].Split('/')[0];

    private static bool ValidComponent(string component)
    {
        if (component is "" or "." or "..")
            return false;
        if (component.Length > 255)
            return false;
        return !component.Any(character => char.IsControl(character) || character == '\\');
    }

    private static bool Overlaps(string first, string second)
    {
        if (first == second)
            return true;
        var firstContainsSecond = second.StartsWith(first + "/", StringComparison.Ordinal);
        var secondContainsFirst = first.StartsWith(second + "/", StringComparison.Ordinal);
        return firstContainsSecond || secondContainsFirst;
    }

    private static string Combine(string root, string relative) => relative.Length == 0 ? root : root + "/" + relative;

    public override string ToString() => "paths.layout";
}

public enum RemotePathKind
{
    Missing,
    Directory,
    NotDirectory,
    Unavailable,
}

public enum RemotePathCanonical
{
    Same,
    Redirected,
    Unavailable,
}

public enum RemotePathContents
{
    NotApplicable,
    Empty,
    NotEmpty,
    Unavailable,
}

/// <summary>Read-only facts about one requested path. For a missing path, canonical form,
/// device, owner and mode describe its nearest existing ancestor <see cref="AncestorDepth"/>
/// levels up; NotDirectory with a positive depth means that ancestor is not a directory.
/// Unavailable means the SSH user could not observe it, not that it is absent.</summary>
public sealed record RemotePathObservation(
    RemotePathRole Role,
    RemotePathKind Kind,
    int AncestorDepth,
    RemotePathCanonical Canonical,
    long? Device,
    int? OwnerUid,
    int? OwnerGid,
    int? Mode,
    RemotePathContents Contents)
{
    /// <summary>Octal 7777: permission, setuid, setgid and sticky bits.</summary>
    public const int MaximumMode = 0xFFF;

    public bool IsValid
    {
        get
        {
            if (!Enum.IsDefined(Role) || !Enum.IsDefined(Kind))
                return false;
            if (!Enum.IsDefined(Canonical) || !Enum.IsDefined(Contents))
                return false;
            if (AncestorDepth is < 0 or > MediaPathLayout.MaximumComponents)
                return false;
            return Kind == RemotePathKind.Unavailable ? UnavailableShape() : ObservedShape();
        }
    }

    private bool UnavailableShape()
    {
        var noFacts = Device is null && OwnerUid is null && OwnerGid is null && Mode is null;
        var noState = Canonical == RemotePathCanonical.Unavailable && Contents == RemotePathContents.Unavailable;
        return noFacts && noState;
    }

    private bool ObservedShape()
    {
        var statPresent = Device is >= 0 && OwnerUid is >= 0 && OwnerGid is >= 0;
        var modeValid = Mode is >= 0 and <= MaximumMode;
        var depthMatchesKind = Kind switch
        {
            RemotePathKind.Missing => AncestorDepth > 0,
            RemotePathKind.Directory => AncestorDepth == 0,
            _ => true,
        };
        var contentsMatchKind = Kind == RemotePathKind.Directory
            ? Contents != RemotePathContents.NotApplicable
            : Contents == RemotePathContents.NotApplicable;
        return statPresent && modeValid && depthMatchesKind && contentsMatchKind;
    }
}

/// <summary>Immutable read-only observation of one layout, bound to the approved host key and the
/// build-time profile. It does not create, chmod or adopt anything and is not an Apply permission.</summary>
public sealed class RemotePathSnapshot
{
    public MediaPathLayout Layout { get; }

    public SshHostKey HostKey { get; }

    public string ProfileFingerprint { get; }

    public DateTimeOffset ObservedAt { get; }

    public IReadOnlyList<RemotePathObservation> Observations { get; }

    public string EnvironmentFingerprint { get; }

    public RemotePathSnapshot(MediaPathLayout layout, SshHostKey hostKey, string profileFingerprint,
        DateTimeOffset observedAt, IEnumerable<RemotePathObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(observations);
        if (hostKey is not { IsValid: true } || !ResourceIdentity.IsFingerprint(profileFingerprint))
            throw new ArgumentException("paths.binding.invalid");
        var items = observations.Take(layout.Entries.Count + 1).ToArray();
        if (items.Any(item => item is not { IsValid: true }))
            throw new ArgumentException("paths.observation.invalid");
        var roles = items.Select(item => item.Role);
        if (!roles.SequenceEqual(layout.Entries.Select(entry => entry.Role)))
            throw new ArgumentException("paths.observation.incomplete");

        Layout = layout;
        HostKey = hostKey;
        ProfileFingerprint = profileFingerprint;
        ObservedAt = observedAt.ToUniversalTime();
        Observations = Array.AsReadOnly(items);
        EnvironmentFingerprint = ConfigurationRequest.Fingerprint(new
        {
            HostKey, ProfileFingerprint, layout.DataRoot, layout.ConfigurationRoot, Observations = items,
        });
    }

    public RemotePathObservation Observation(RemotePathRole role) => Observations.Single(item => item.Role == role);

    public override string ToString() => "paths.snapshot";
}

/// <summary>Pure path assessment. DSM ACL and container PUID/PGID access are never inferred from
/// POSIX mode bits; they stay NotChecked/Unverified until the hardware and profile contracts exist.</summary>
public static class RemotePathAssessment
{
    public static IReadOnlyList<PreflightCheck> Evaluate(RemotePathSnapshot snapshot)
    {
        var checks = new List<PreflightCheck>();
        foreach (var observation in snapshot.Observations)
            checks.Add(PathCheck(snapshot.Layout, observation));
        checks.Add(ConfigurationContentsCheck(snapshot.Observation(RemotePathRole.ConfigurationRoot)));
        checks.Add(HardlinkDeviceCheck(snapshot));
        checks.Add(new("paths.permissions", PreflightCheckStatus.Unverified));
        checks.Add(new("paths.dsm-acl", PreflightCheckStatus.NotChecked));
        return checks.AsReadOnly();
    }

    private static PreflightCheck PathCheck(MediaPathLayout layout, RemotePathObservation observation)
    {
        var code = "paths." + Name(observation.Role);
        if (observation.Kind == RemotePathKind.Unavailable)
            return new(code + ".unavailable", PreflightCheckStatus.Blocking);
        if (observation.Kind == RemotePathKind.NotDirectory)
            return new(code + ".not-directory", PreflightCheckStatus.Blocking);
        if (observation.Canonical == RemotePathCanonical.Unavailable)
            return new(code + ".unavailable", PreflightCheckStatus.Blocking);
        if (observation.Canonical == RemotePathCanonical.Redirected)
            return new(code + ".redirected", PreflightCheckStatus.Blocking);
        if (!AncestorInsideVolume(layout, observation))
            return new(code + ".volume-missing", PreflightCheckStatus.Blocking);
        return new(code, PreflightCheckStatus.Passed);
    }

    /// <summary>Only the volume mount itself or a directory below it may be the existing ancestor.</summary>
    private static bool AncestorInsideVolume(MediaPathLayout layout, RemotePathObservation observation)
    {
        var path = layout.Entries.Single(entry => entry.Role == observation.Role).HostPath;
        var components = path[1..].Split('/').Length;
        return components - observation.AncestorDepth >= 1;
    }

    /// <summary>A non-empty configuration root may hold application databases or a foreign stack.
    /// Only ownership evidence (#51–#53) may later allow continuing an own session.</summary>
    private static PreflightCheck ConfigurationContentsCheck(RemotePathObservation observation)
    {
        if (observation.Kind == RemotePathKind.Missing)
            return new("paths.configuration-root.absent", PreflightCheckStatus.Passed);
        if (observation.Contents == RemotePathContents.Empty)
            return new("paths.configuration-root.empty", PreflightCheckStatus.Passed);
        if (observation.Contents == RemotePathContents.NotEmpty)
            return new("paths.configuration-root.ownership-required", PreflightCheckStatus.Blocking);
        return new("paths.configuration-root.contents-unavailable", PreflightCheckStatus.Blocking);
    }

    /// <summary>Downloads and the library must share one filesystem device (one DSM shared folder /
    /// btrfs subvolume) so Arr imports can hardlink. A missing directory inherits its ancestor's device.</summary>
    private static PreflightCheck HardlinkDeviceCheck(RemotePathSnapshot snapshot)
    {
        var data = snapshot.Observations.Where(item => item.Role != RemotePathRole.ConfigurationRoot).ToArray();
        if (data.Any(item => item.Device is null))
            return new("paths.hardlink-device.unavailable", PreflightCheckStatus.Blocking);
        if (data.Select(item => item.Device).Distinct().Count() != 1)
            return new("paths.hardlink-device.mismatch", PreflightCheckStatus.Blocking);
        return new("paths.hardlink-device", PreflightCheckStatus.Passed);
    }

    private static string Name(RemotePathRole role) => role switch
    {
        RemotePathRole.DataRoot => "data-root",
        RemotePathRole.Torrents => "torrents",
        RemotePathRole.TorrentsMovies => "torrents-movies",
        RemotePathRole.TorrentsSeries => "torrents-series",
        RemotePathRole.Media => "media",
        RemotePathRole.MediaMovies => "media-movies",
        RemotePathRole.MediaSeries => "media-series",
        RemotePathRole.ConfigurationRoot => "configuration-root",
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };
}

/// <summary>Ready means the observation completed, never that the layout is usable. Inspect the
/// assessment. A failed or partial inspection returns no facts.</summary>
public sealed record RemotePathResult
{
    public SshProbeStatus Status { get; }

    public SshHostKey? HostKey { get; }

    public RemotePathSnapshot? Snapshot { get; }

    private RemotePathResult(SshProbeStatus status, SshHostKey? key, RemotePathSnapshot? snapshot)
    {
        Status = status;
        HostKey = key;
        Snapshot = snapshot;
    }

    public static RemotePathResult Observed(RemotePathSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new(SshProbeStatus.Ready, snapshot.HostKey, snapshot);
    }

    public static RemotePathResult Failed(SshProbeResult failure)
    {
        if (failure.Status == SshProbeStatus.Ready)
            throw new ArgumentException("paths.failure.invalid");
        return new(failure.Status, failure.HostKey, null);
    }
}
