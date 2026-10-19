namespace HhHomelab.Wizard.Domain;

public enum PreflightFactStatus
{
    Available,
    Missing,
    Unavailable,
}

/// <summary>An observed version is not a claim of compatibility. Missing and unreadable differ.</summary>
public sealed record PreflightVersion(PreflightFactStatus Status, string? Version = null)
{
    public bool IsValid => Status switch
    {
        PreflightFactStatus.Available => ValidVersion(Version),
        PreflightFactStatus.Missing or PreflightFactStatus.Unavailable => Version is null,
        _ => false,
    };

    private static bool ValidVersion(string? version)
    {
        if (version is not { Length: > 0 and <= 80 })
            return false;
        if (!char.IsAsciiDigit(version[0]))
            return false;
        return version.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' or '+');
    }
}

/// <summary>DSM model name from a candidate file contract; not a hardware compatibility claim.</summary>
public sealed record PreflightModel(PreflightFactStatus Status, string? Name = null)
{
    public bool IsValid => Status switch
    {
        PreflightFactStatus.Available => ValidName(Name),
        PreflightFactStatus.Missing or PreflightFactStatus.Unavailable => Name is null,
        _ => false,
    };

    private static bool ValidName(string? name)
    {
        if (name is not { Length: > 0 and <= 40 })
            return false;
        if (!char.IsAsciiLetterOrDigit(name[0]))
            return false;
        return name.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' or '+');
    }
}

/// <summary>Capacity of one DSM data volume mount point. Free space drifts naturally and is not
/// part of the environment identity.</summary>
public sealed record PreflightVolume(string Mount, long TotalBytes, long AvailableBytes)
{
    public bool IsValid
    {
        get
        {
            if (!IsVolumeMount(Mount))
                return false;
            var capacityPositive = TotalBytes > 0;
            var availableWithinCapacity = AvailableBytes >= 0 && AvailableBytes <= TotalBytes;
            return capacityPositive && availableWithinCapacity;
        }
    }

    public static bool IsVolumeMount(string? mount)
    {
        if (mount is not { Length: > 7 and <= 11 } || !mount.StartsWith("/volume", StringComparison.Ordinal))
            return false;
        var number = mount[7..];
        if (number[0] == '0')
            return false;
        return number.All(char.IsAsciiDigit);
    }
}

/// <summary>Bounded host resources: model, TCP listeners of the SSH session network namespace and
/// DSM volume capacity. Listener absence is not proof that a port is free in every namespace.</summary>
public sealed class NasHostResources
{
    public const int MaximumListeningPorts = 256;

    public const int MaximumVolumes = 16;

    public PreflightModel Model { get; }

    public PreflightFactStatus ListeningStatus { get; }

    public IReadOnlyList<int> ListeningTcpPorts { get; }

    public PreflightFactStatus VolumeStatus { get; }

    public IReadOnlyList<PreflightVolume> Volumes { get; }

    public NasHostResources(PreflightModel model, PreflightFactStatus listeningStatus, IEnumerable<int> listeningTcpPorts,
        PreflightFactStatus volumeStatus, IEnumerable<PreflightVolume> volumes)
    {
        if (model is not { IsValid: true })
            throw new ArgumentException("preflight.model.invalid");
        ArgumentNullException.ThrowIfNull(listeningTcpPorts);
        ArgumentNullException.ThrowIfNull(volumes);
        var ports = listeningTcpPorts.Take(MaximumListeningPorts + 1).ToArray();
        var mounts = volumes.Take(MaximumVolumes + 1).ToArray();
        if (!ObservedStatus(listeningStatus) || !ObservedStatus(volumeStatus))
            throw new ArgumentException("preflight.resources.invalid");
        if (ports.Length > MaximumListeningPorts || ports.Any(port => port is < 1 or > 65535))
            throw new ArgumentException("preflight.ports.invalid");
        if (ports.Distinct().Count() != ports.Length)
            throw new ArgumentException("preflight.ports.duplicate");
        if (listeningStatus != PreflightFactStatus.Available && ports.Length != 0)
            throw new ArgumentException("preflight.ports.incomplete");
        if (mounts.Length > MaximumVolumes || mounts.Any(volume => volume is not { IsValid: true }))
            throw new ArgumentException("preflight.volumes.invalid");
        if (mounts.Select(volume => volume.Mount).Distinct(StringComparer.Ordinal).Count() != mounts.Length)
            throw new ArgumentException("preflight.volumes.duplicate");
        if (volumeStatus != PreflightFactStatus.Available && mounts.Length != 0)
            throw new ArgumentException("preflight.volumes.incomplete");

        Model = model;
        ListeningStatus = listeningStatus;
        ListeningTcpPorts = Array.AsReadOnly(ports.Order().ToArray());
        VolumeStatus = volumeStatus;
        Volumes = Array.AsReadOnly(mounts.OrderBy(volume => volume.Mount.Length).ThenBy(volume => volume.Mount, StringComparer.Ordinal).ToArray());
    }

    private static bool ObservedStatus(PreflightFactStatus status) =>
        status is PreflightFactStatus.Available or PreflightFactStatus.Unavailable;

    public override string ToString() => "preflight.resources";
}

/// <summary>Bounded read-only facts; never container configuration, credentials or arbitrary output.</summary>
public sealed class NasPreflightFacts
{
    public const int MaximumContainers = 128;

    public string Kernel { get; }

    public string Architecture { get; }

    public DateTimeOffset ServerTime { get; }

    public PreflightVersion Dsm { get; }

    public PreflightVersion ContainerManager { get; }

    public PreflightVersion DockerServer { get; }

    public PreflightVersion Compose { get; }

    public PreflightFactStatus ContainerInventoryStatus { get; }

    public IReadOnlyList<string> ContainerIds { get; }

    public NasHostResources Resources { get; }

    public NasPreflightFacts(string kernel, string architecture, DateTimeOffset serverTime,
        PreflightVersion dsm, PreflightVersion containerManager, PreflightVersion dockerServer,
        PreflightVersion compose, PreflightFactStatus inventoryStatus, IEnumerable<string> containerIds,
        NasHostResources resources)
    {
        if (!ValidIdentifier(kernel) || !ValidIdentifier(architecture))
            throw new ArgumentException("preflight.identity.invalid");
        PreflightVersion[] versions = [dsm, containerManager, dockerServer, compose];
        if (versions.Any(version => version is not { IsValid: true }))
            throw new ArgumentException("preflight.version.invalid");
        if (inventoryStatus is not (PreflightFactStatus.Available or PreflightFactStatus.Unavailable))
            throw new ArgumentException("preflight.inventory.invalid");
        ArgumentNullException.ThrowIfNull(containerIds);
        var ids = containerIds.Take(MaximumContainers + 1).ToArray();
        if (ids.Length > MaximumContainers || ids.Any(id => !ResourceIdentity.IsFingerprint(id)))
            throw new ArgumentException("preflight.inventory.invalid");
        if (ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ids.Length)
            throw new ArgumentException("preflight.inventory.duplicate");
        if (inventoryStatus != PreflightFactStatus.Available && ids.Length != 0)
            throw new ArgumentException("preflight.inventory.incomplete");
        if (inventoryStatus == PreflightFactStatus.Available && dockerServer.Status != PreflightFactStatus.Available)
            throw new ArgumentException("preflight.inventory.inconsistent");
        ArgumentNullException.ThrowIfNull(resources);

        Kernel = kernel;
        Architecture = architecture;
        ServerTime = serverTime.ToUniversalTime();
        Dsm = dsm;
        ContainerManager = containerManager;
        DockerServer = dockerServer;
        Compose = compose;
        ContainerInventoryStatus = inventoryStatus;
        ContainerIds = Array.AsReadOnly(ids.Select(id => id.ToLowerInvariant()).Order(StringComparer.Ordinal).ToArray());
        Resources = resources;
    }

    private static bool ValidIdentifier(string value) => value is { Length: > 0 and <= 64 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.');

    public override string ToString() => "preflight.facts";
}

/// <summary>An immutable observation bound to the approved server key, build-time profile and time.
/// Freshness does not replace a new observation before Apply or prove resource ownership.</summary>
public sealed class NasPreflightSnapshot
{
    public SshHostKey HostKey { get; }

    public string PrincipalFingerprint { get; }

    public string ProfileFingerprint { get; }

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset CompletedAt { get; }

    public NasPreflightFacts Facts { get; }

    public string EnvironmentFingerprint { get; }

    public NasPreflightSnapshot(SshConnectionTarget target, SshHostKey hostKey, string profileFingerprint, DateTimeOffset startedAt,
        DateTimeOffset completedAt, NasPreflightFacts facts)
    {
        if (target is not { IsValid: true })
            throw new ArgumentException("preflight.target.invalid");
        if (hostKey is not { IsValid: true } || !ResourceIdentity.IsFingerprint(profileFingerprint))
            throw new ArgumentException("preflight.binding.invalid");
        if (hostKey.EndpointFingerprint != target.EndpointFingerprint)
            throw new ArgumentException("preflight.endpoint.invalid");
        if (completedAt < startedAt)
            throw new ArgumentException("preflight.time.invalid");
        ArgumentNullException.ThrowIfNull(facts);
        HostKey = hostKey;
        PrincipalFingerprint = PrincipalIdentity(target);
        ProfileFingerprint = profileFingerprint;
        StartedAt = startedAt.ToUniversalTime();
        CompletedAt = completedAt.ToUniversalTime();
        Facts = facts;
        var resources = facts.Resources;
        EnvironmentFingerprint = ConfigurationRequest.Fingerprint(new
        {
            HostKey, PrincipalFingerprint, ProfileFingerprint, facts.Kernel, facts.Architecture, facts.Dsm,
            facts.ContainerManager, facts.DockerServer, facts.Compose,
            facts.ContainerInventoryStatus, facts.ContainerIds,
            resources.Model, resources.ListeningStatus, resources.ListeningTcpPorts, resources.VolumeStatus,
            Volumes = resources.Volumes.Select(volume => new { volume.Mount, volume.TotalBytes }).ToArray(),
        });
    }

    public bool IsCurrentFor(SshConnectionTarget target, SshHostKey approvedKey, string profileFingerprint, DateTimeOffset now, TimeSpan maximumAge)
    {
        if (maximumAge <= TimeSpan.Zero || maximumAge > TimeSpan.FromMinutes(5))
            return false;
        if (target is not { IsValid: true })
            return false;
        if (PrincipalFingerprint != PrincipalIdentity(target))
            return false;
        if (HostKey != approvedKey || ProfileFingerprint != profileFingerprint)
            return false;
        return now >= CompletedAt && now - StartedAt <= maximumAge;
    }

    private static string PrincipalIdentity(SshConnectionTarget target) =>
        ConfigurationRequest.Fingerprint(new { target.EndpointFingerprint, target.Username });

    public override string ToString() => "preflight.snapshot";
}

public enum PreflightCheckStatus
{
    Passed,
    Blocking,
    Unverified,
    NotChecked,
}

public sealed record PreflightCheck(string Code, PreflightCheckStatus Status);

/// <summary>Plan-derived resource requirements: published host TCP ports and the free capacity needed
/// on the selected DSM volume. The preflight never invents them from defaults.</summary>
public sealed class PreflightRequirements
{
    public const int MaximumPorts = 64;

    public IReadOnlyList<int> TcpPorts { get; }

    public string VolumeMount { get; }

    public long MinimumFreeBytes { get; }

    public PreflightRequirements(IEnumerable<int> tcpPorts, string volumeMount, long minimumFreeBytes)
    {
        ArgumentNullException.ThrowIfNull(tcpPorts);
        var ports = tcpPorts.Take(MaximumPorts + 1).ToArray();
        if (ports.Length > MaximumPorts || ports.Any(port => port is < 1 or > 65535))
            throw new ArgumentException("preflight.requirements.ports.invalid");
        if (ports.Distinct().Count() != ports.Length)
            throw new ArgumentException("preflight.requirements.ports.duplicate");
        if (!PreflightVolume.IsVolumeMount(volumeMount))
            throw new ArgumentException("preflight.requirements.volume.invalid");
        if (minimumFreeBytes <= 0)
            throw new ArgumentException("preflight.requirements.space.invalid");
        TcpPorts = Array.AsReadOnly(ports.Order().ToArray());
        VolumeMount = volumeMount;
        MinimumFreeBytes = minimumFreeBytes;
    }
}

/// <summary>Prerequisite assessment. No version matrix/hardware/path/ownership evidence is invented
/// from a successful SSH connection or an empty container inventory. Without plan requirements
/// ports and disk space stay NotChecked even though they were observed.</summary>
public static class NasPreflightAssessment
{
    public static IReadOnlyList<PreflightCheck> Evaluate(NasPreflightSnapshot snapshot) => Evaluate(snapshot, null);

    public static IReadOnlyList<PreflightCheck> Evaluate(NasPreflightSnapshot snapshot, PreflightRequirements? requirements)
    {
        var facts = snapshot.Facts;
        var clockWithinWindow = facts.ServerTime - snapshot.StartedAt >= -TimeSpan.FromMinutes(2)
            && facts.ServerTime - snapshot.CompletedAt <= TimeSpan.FromMinutes(2);
        var checks = new List<PreflightCheck>
        {
            Check("preflight.kernel", facts.Kernel == "Linux"),
            Check("preflight.architecture", facts.Architecture == "x86_64"),
            Check("preflight.clock-window", clockWithinWindow),
            VersionCheck("preflight.dsm", facts.Dsm.Status),
            VersionCheck("preflight.container-manager", facts.ContainerManager.Status),
            VersionCheck("preflight.docker-server", facts.DockerServer.Status),
            VersionCheck("preflight.compose", facts.Compose.Status),
            InventoryCheck(facts),
            VersionCheck("preflight.nas-model", facts.Resources.Model.Status),
            new("preflight.paths-acl", PreflightCheckStatus.NotChecked),
            DiskSpaceCheck(facts.Resources, requirements),
            PortsCheck(facts.Resources, requirements),
            new("preflight.ownership", PreflightCheckStatus.NotChecked),
        };
        return checks.AsReadOnly();
    }

    /// <summary>Required ports that already have a TCP listener; empty when listeners were not observed.</summary>
    public static IReadOnlyList<int> ConflictingPorts(NasPreflightSnapshot snapshot, PreflightRequirements requirements)
    {
        var listening = snapshot.Facts.Resources.ListeningTcpPorts;
        return Array.AsReadOnly(requirements.TcpPorts.Where(port => listening.Contains(port)).ToArray());
    }

    private static PreflightCheck Check(string code, bool passed) => new(code,
        passed ? PreflightCheckStatus.Passed : PreflightCheckStatus.Blocking);

    private static PreflightCheck VersionCheck(string code, PreflightFactStatus status) => new(code,
        status == PreflightFactStatus.Available ? PreflightCheckStatus.Unverified : PreflightCheckStatus.Blocking);

    private static PreflightCheck InventoryCheck(NasPreflightFacts facts)
    {
        if (facts.ContainerInventoryStatus != PreflightFactStatus.Available)
            return new("preflight.containers.unavailable", PreflightCheckStatus.Blocking);
        if (facts.ContainerIds.Count != 0)
            return new("preflight.containers.ownership-required", PreflightCheckStatus.Blocking);
        return new("preflight.containers.empty", PreflightCheckStatus.Passed);
    }

    private static PreflightCheck DiskSpaceCheck(NasHostResources resources, PreflightRequirements? requirements)
    {
        if (requirements is null)
            return new("preflight.disk-space", PreflightCheckStatus.NotChecked);
        if (resources.VolumeStatus != PreflightFactStatus.Available)
            return new("preflight.disk-space.unavailable", PreflightCheckStatus.Blocking);
        var volume = resources.Volumes.FirstOrDefault(candidate => candidate.Mount == requirements.VolumeMount);
        if (volume is null)
            return new("preflight.disk-space.volume-missing", PreflightCheckStatus.Blocking);
        if (volume.AvailableBytes < requirements.MinimumFreeBytes)
            return new("preflight.disk-space.insufficient", PreflightCheckStatus.Blocking);
        return new("preflight.disk-space", PreflightCheckStatus.Passed);
    }

    private static PreflightCheck PortsCheck(NasHostResources resources, PreflightRequirements? requirements)
    {
        if (requirements is null)
            return new("preflight.ports", PreflightCheckStatus.NotChecked);
        if (resources.ListeningStatus != PreflightFactStatus.Available)
            return new("preflight.ports.unavailable", PreflightCheckStatus.Blocking);
        if (requirements.TcpPorts.Any(port => resources.ListeningTcpPorts.Contains(port)))
            return new("preflight.ports.conflict", PreflightCheckStatus.Blocking);
        return new("preflight.ports", PreflightCheckStatus.Passed);
    }
}

/// <summary>Ready means collection completed, never permission to install. Inspect the assessment.
/// Any failed/partial collection discards all facts; raw SSH output is not returned.</summary>
public sealed record NasPreflightResult
{
    public SshProbeStatus Status { get; }

    public SshHostKey? HostKey { get; }

    public NasPreflightSnapshot? Snapshot { get; }

    private NasPreflightResult(SshProbeStatus status, SshHostKey? key, NasPreflightSnapshot? snapshot)
    {
        Status = status;
        HostKey = key;
        Snapshot = snapshot;
    }

    public static NasPreflightResult Observed(NasPreflightSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new(SshProbeStatus.Ready, snapshot.HostKey, snapshot);
    }

    public static NasPreflightResult Failed(SshProbeResult failure)
    {
        if (failure.Status == SshProbeStatus.Ready)
            throw new ArgumentException("preflight.failure.invalid");
        return new(failure.Status, failure.HostKey, null);
    }
}
