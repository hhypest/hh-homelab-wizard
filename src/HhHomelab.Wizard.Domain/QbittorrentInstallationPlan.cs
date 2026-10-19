namespace HhHomelab.Wizard.Domain;

public enum PlannedResourceKind
{
    Directory,
    File,
    Container,
    Network,
}
public enum PlannedResourceState
{
    PreserveOrCreate,
    AbsentOrOwned,
}
public enum PlannedAddressContext
{
    ComputerToNas,
    HomeAssistantToHost,
    ComposeToService,
}
public enum PlannedPortProtocol
{
    Tcp,
    Udp,
}

public sealed record PlannedResource(PlannedResourceKind Kind, string Location, string? ContainerPath,
    PlannedResourceState ExpectedState, InstallationOwner? Owner);
public sealed record PlannedPublication(PlannedPortProtocol Protocol, string BindAddress, int HostPort, int ContainerPort);
public sealed record PlannedEndpoint(PlannedAddressContext Context, Uri? Address);

/// <summary>Read-only preview, never an authorization or an executable graph. All collections and
/// inputs are immutable; outstanding checks must survive presentation and later execution wiring.</summary>
public sealed class QbittorrentInstallationPlan
{
    public static TimeSpan MaximumEvidenceAge => TimeSpan.FromMinutes(2);

    public QbittorrentInstallationPlan(QbittorrentInstallationRequest request, QbittorrentPlanProfile profile,
        NasPreflightSnapshot preflight, RemotePathSnapshot paths, SshHostKey pin, DateTimeOffset now)
    {
        if (!EvidenceIsBound(request, profile, preflight, paths, pin) || !EvidenceIsFresh(request, profile, preflight, paths, pin, now))
            throw new ArgumentException("plan.evidence.invalid");
        Request = request;
        Profile = profile;
        ObservedAt = now;
        HostKey = pin;
        EvidenceStartedAt = preflight.StartedAt;
        Owner = new(request.InstallationId, profile.ResearchFingerprint);
        Resources = BuildResources(request, profile, Owner);
        Publications = Array.AsReadOnly(new[]
        {
            new PlannedPublication(PlannedPortProtocol.Tcp, request.BindAddress, request.WebUiPort, ContainerWebUiPort),
            new(PlannedPortProtocol.Tcp, request.BindAddress, request.TorrentPort, request.TorrentPort),
            new(PlannedPortProtocol.Udp, request.BindAddress, request.TorrentPort, request.TorrentPort),
        });
        var localAddress = request.BindAddress == request.NasAddress ? request.NasAddress : "127.0.0.1";
        Endpoints = Array.AsReadOnly(new[]
        {
            new PlannedEndpoint(PlannedAddressContext.ComputerToNas, request.BindAddress == "127.0.0.1" ? null : Http(request.NasAddress, request.WebUiPort)),
            new(PlannedAddressContext.HomeAssistantToHost, Http(localAddress, request.WebUiPort)),
            new(PlannedAddressContext.ComposeToService, Http(profile.ContainerName, ContainerWebUiPort)),
        });
        var sameVolume = MediaPathLayout.VolumeOf(request.Layout.DataRoot) == MediaPathLayout.VolumeOf(request.Layout.ConfigurationRoot);
        var configurationSpace = request.ConfigurationFreeBytes + (sameVolume ? request.DataFreeBytes : 0);
        var requirements = new List<PreflightRequirements>
        {
            new([request.WebUiPort, request.TorrentPort], MediaPathLayout.VolumeOf(request.Layout.ConfigurationRoot), configurationSpace),
        };
        if (!sameVolume)
            requirements.Add(new([], MediaPathLayout.VolumeOf(request.Layout.DataRoot), request.DataFreeBytes));
        VolumeRequirements = requirements.AsReadOnly();
        var checks = NasPreflightAssessment.Evaluate(preflight, requirements[0])
            .Where(check => check.Code is not "preflight.ownership" and not "preflight.paths-acl").ToList();
        if (!sameVolume)
        {
            var disk = NasPreflightAssessment.Evaluate(preflight, requirements[1]).Single(check => check.Code.StartsWith("preflight.disk-space", StringComparison.Ordinal));
            checks.Add(new("plan.data-space" + disk.Code["preflight.disk-space".Length..], disk.Status));
        }
        checks.AddRange(RemotePathAssessment.Evaluate(paths, Owner));
        checks.Add(UnixIdentityCheck("plan.puid.ssh-user", request.UserId, paths.Identity.UserId));
        checks.Add(UnixIdentityCheck("plan.pgid.ssh-primary-group", request.GroupId, paths.Identity.PrimaryGroupId));
        checks.Add(new("plan.udp-ports", PreflightCheckStatus.NotChecked));
        checks.Add(new("plan.configuration-children", PreflightCheckStatus.NotChecked));
        checks.Add(new("plan.network-name", PreflightCheckStatus.NotChecked));
        checks.Add(new("plan.address-reachability", PreflightCheckStatus.NotChecked));
        checks.Add(new("plan.timezone", PreflightCheckStatus.Unverified));
        checks.Add(new("plan.docker-storage", PreflightCheckStatus.NotChecked));
        if (request.BindAddress == "127.0.0.1")
            checks.Add(new("plan.computer-access.proxy-required", PreflightCheckStatus.Blocking));
        // .env carries the data root literally in single quotes and Compose splits short volumes on ':'.
        if (request.Layout.DataRoot.Contains('\'', StringComparison.Ordinal) || request.Layout.DataRoot.Contains(':', StringComparison.Ordinal))
            checks.Add(new("plan.data-root.unsupported-characters", PreflightCheckStatus.Blocking));
        Checks = checks.AsReadOnly();
        Fingerprint = ConfigurationRequest.Fingerprint(new
        {
            Request, Profile = profile.Fingerprint,
            Preflight = preflight.EnvironmentFingerprint, Paths = paths.EnvironmentFingerprint, Checks,
        });
    }

    public QbittorrentInstallationRequest Request { get; }
    public QbittorrentPlanProfile Profile { get; }
    /// <summary>Required WEBUI_PORT value inside the container, aligned with its published Host port.</summary>
    public int ContainerWebUiPort => Request.WebUiPort;
    public InstallationOwner Owner { get; }
    public DateTimeOffset ObservedAt { get; }
    /// <summary>The approved host key every snapshot of this plan was collected with.</summary>
    public SshHostKey HostKey { get; }
    /// <summary>Start of the oldest evidence; freshness of any later use is measured from here.</summary>
    public DateTimeOffset EvidenceStartedAt { get; }
    public IReadOnlyList<PlannedResource> Resources { get; }
    public IReadOnlyList<PlannedPublication> Publications { get; }
    public IReadOnlyList<PlannedEndpoint> Endpoints { get; }
    public IReadOnlyList<PreflightRequirements> VolumeRequirements { get; }
    public IReadOnlyList<PreflightCheck> Checks { get; }
    public string Fingerprint { get; }
    public bool HasBlockingChecks => Checks.Any(check => check.Status == PreflightCheckStatus.Blocking);
    public bool HasUnresolvedChecks => Checks.Any(check => check.Status != PreflightCheckStatus.Passed);

    public static bool EvidenceIsBound(QbittorrentInstallationRequest request, QbittorrentPlanProfile profile,
        NasPreflightSnapshot preflight, RemotePathSnapshot paths, SshHostKey pin) =>
        pin is { IsValid: true } && pin.EndpointFingerprint == request.Target.EndpointFingerprint
        && preflight.HostKey == pin && preflight.ProfileFingerprint == profile.ResearchFingerprint
        && paths.IsBoundTo(request.Target, pin, profile.ResearchFingerprint)
        && preflight.PrincipalFingerprint == paths.PrincipalFingerprint
        && paths.Layout.DataRoot == request.Layout.DataRoot && paths.Layout.ConfigurationRoot == request.Layout.ConfigurationRoot;

    public static bool EvidenceIsFresh(QbittorrentInstallationRequest request, QbittorrentPlanProfile profile,
        NasPreflightSnapshot preflight, RemotePathSnapshot paths, SshHostKey pin, DateTimeOffset now) =>
        preflight.IsCurrentFor(request.Target, pin, profile.ResearchFingerprint, now, MaximumEvidenceAge)
        && paths.ObservedAt >= preflight.CompletedAt && now >= paths.ObservedAt && now - paths.ObservedAt <= MaximumEvidenceAge;

    private static IReadOnlyList<PlannedResource> BuildResources(QbittorrentInstallationRequest request, QbittorrentPlanProfile profile, InstallationOwner owner)
    {
        var resources = request.Layout.Entries.Where(entry => entry.Role != RemotePathRole.ConfigurationRoot)
            .Select(entry => new PlannedResource(PlannedResourceKind.Directory, entry.HostPath, entry.ContainerPath,
                PlannedResourceState.PreserveOrCreate, null)).ToList();
        var directories = new HashSet<string>(StringComparer.Ordinal);
        void AddDirectory(string path, string? containerPath = null)
        {
            if (!MediaPathLayout.IsVolumePath(path))
                throw new ArgumentException("plan.path.invalid");
            if (directories.Add(path))
                resources.Add(new(PlannedResourceKind.Directory, path, containerPath, PlannedResourceState.AbsentOrOwned, owner));
        }
        string Resolve(string relative)
        {
            var path = request.Layout.ConfigurationRoot + "/" + relative;
            if (!MediaPathLayout.IsVolumePath(path))
                throw new ArgumentException("plan.path.invalid");
            return path;
        }
        void AddParents(string relative)
        {
            var parts = relative.Split('/');
            for (var length = 1; length < parts.Length; length++)
                AddDirectory(Resolve(string.Join('/', parts.Take(length))));
        }
        AddDirectory(request.Layout.ConfigurationRoot);
        var markerPath = OwnershipMarker.DirectoryName + "/" + OwnershipMarker.FileName;
        foreach (var relative in new[] { profile.ComposePath, profile.EnvironmentPath, profile.ConfigurationPath, profile.WatchPath, markerPath })
            AddParents(relative);
        AddDirectory(Resolve(profile.ConfigurationPath), "/config");
        AddDirectory(Resolve(profile.WatchPath), "/watch");
        foreach (var relative in new[] { profile.ComposePath, profile.EnvironmentPath, markerPath })
        {
            var path = Resolve(relative);
            if (directories.Contains(path))
                throw new ArgumentException("plan.path.collision");
            resources.Add(new(PlannedResourceKind.File, path, null, PlannedResourceState.AbsentOrOwned, owner));
        }
        resources.Add(new(PlannedResourceKind.Container, profile.ContainerName, null, PlannedResourceState.AbsentOrOwned, owner));
        resources.Add(new(PlannedResourceKind.Network, request.NetworkName, null, PlannedResourceState.AbsentOrOwned, owner));
        return resources.AsReadOnly();
    }

    private static Uri Http(string host, int port) => new UriBuilder("http", host, port).Uri;

    // The first graph creates configuration mounts as the SSH user, without chown/sudo. Only
    // this identity policy is supported; a matching ID is not proof of write access or DSM ACL.
    private static PreflightCheck UnixIdentityCheck(string code, int selected, int observed) => selected == observed
        ? new(code, PreflightCheckStatus.Passed)
        : new(code + ".mismatch", PreflightCheckStatus.Blocking);

    public override string ToString() => "plan.qbittorrent-preview";
}
