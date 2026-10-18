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

/// <summary>Bounded read-only facts; never container configuration, credentials or arbitrary output.</summary>
public sealed class NasPreflightFacts
{
    public string Kernel { get; }

    public string Architecture { get; }

    public DateTimeOffset ServerTime { get; }

    public PreflightVersion Dsm { get; }

    public PreflightVersion ContainerManager { get; }

    public PreflightVersion DockerServer { get; }

    public PreflightVersion Compose { get; }

    public PreflightFactStatus ContainerInventoryStatus { get; }

    public IReadOnlyList<string> ContainerIds { get; }

    public NasPreflightFacts(string kernel, string architecture, DateTimeOffset serverTime,
        PreflightVersion dsm, PreflightVersion containerManager, PreflightVersion dockerServer,
        PreflightVersion compose, PreflightFactStatus inventoryStatus, IEnumerable<string> containerIds)
    {
        if (!ValidIdentifier(kernel) || !ValidIdentifier(architecture))
            throw new ArgumentException("preflight.identity.invalid");
        PreflightVersion[] versions = [dsm, containerManager, dockerServer, compose];
        if (versions.Any(version => version is not { IsValid: true }))
            throw new ArgumentException("preflight.version.invalid");
        if (inventoryStatus is not (PreflightFactStatus.Available or PreflightFactStatus.Unavailable))
            throw new ArgumentException("preflight.inventory.invalid");
        ArgumentNullException.ThrowIfNull(containerIds);
        var ids = containerIds.Take(129).ToArray();
        if (ids.Length > 128 || ids.Any(id => !ResourceIdentity.IsFingerprint(id)))
            throw new ArgumentException("preflight.inventory.invalid");
        if (ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ids.Length)
            throw new ArgumentException("preflight.inventory.duplicate");
        if (inventoryStatus != PreflightFactStatus.Available && ids.Length != 0)
            throw new ArgumentException("preflight.inventory.incomplete");
        if (inventoryStatus == PreflightFactStatus.Available && dockerServer.Status != PreflightFactStatus.Available)
            throw new ArgumentException("preflight.inventory.inconsistent");

        Kernel = kernel;
        Architecture = architecture;
        ServerTime = serverTime.ToUniversalTime();
        Dsm = dsm;
        ContainerManager = containerManager;
        DockerServer = dockerServer;
        Compose = compose;
        ContainerInventoryStatus = inventoryStatus;
        ContainerIds = Array.AsReadOnly(ids.Select(id => id.ToLowerInvariant()).Order(StringComparer.Ordinal).ToArray());
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
        EnvironmentFingerprint = ConfigurationRequest.Fingerprint(new
        {
            HostKey, PrincipalFingerprint, ProfileFingerprint, facts.Kernel, facts.Architecture, facts.Dsm,
            facts.ContainerManager, facts.DockerServer, facts.Compose,
            facts.ContainerInventoryStatus, facts.ContainerIds,
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

/// <summary>First prerequisite assessment only. No version matrix/hardware/path/ownership evidence
/// is invented from a successful SSH connection or an empty container inventory.</summary>
public static class NasPreflightAssessment
{
    public static IReadOnlyList<PreflightCheck> Evaluate(NasPreflightSnapshot snapshot)
    {
        var facts = snapshot.Facts;
        var clockWithinWindow = facts.ServerTime - snapshot.StartedAt >= -TimeSpan.FromMinutes(2)
            && facts.ServerTime - snapshot.CompletedAt <= TimeSpan.FromMinutes(2);
        var checks = new List<PreflightCheck>
        {
            Check("preflight.kernel", facts.Kernel == "Linux"),
            Check("preflight.architecture", facts.Architecture == "x86_64"),
            Check("preflight.clock-window", clockWithinWindow),
            VersionCheck("preflight.dsm", facts.Dsm),
            VersionCheck("preflight.container-manager", facts.ContainerManager),
            VersionCheck("preflight.docker-server", facts.DockerServer),
            VersionCheck("preflight.compose", facts.Compose),
            InventoryCheck(facts),
            new("preflight.nas-model", PreflightCheckStatus.NotChecked),
            new("preflight.paths-acl", PreflightCheckStatus.NotChecked),
            new("preflight.disk-space", PreflightCheckStatus.NotChecked),
            new("preflight.ports", PreflightCheckStatus.NotChecked),
            new("preflight.ownership", PreflightCheckStatus.NotChecked),
        };
        return checks.AsReadOnly();
    }

    private static PreflightCheck Check(string code, bool passed) => new(code,
        passed ? PreflightCheckStatus.Passed : PreflightCheckStatus.Blocking);

    private static PreflightCheck VersionCheck(string code, PreflightVersion version) => new(code,
        version.Status == PreflightFactStatus.Available ? PreflightCheckStatus.Unverified : PreflightCheckStatus.Blocking);

    private static PreflightCheck InventoryCheck(NasPreflightFacts facts)
    {
        if (facts.ContainerInventoryStatus != PreflightFactStatus.Available)
            return new("preflight.containers.unavailable", PreflightCheckStatus.Blocking);
        if (facts.ContainerIds.Count != 0)
            return new("preflight.containers.ownership-required", PreflightCheckStatus.Blocking);
        return new("preflight.containers.empty", PreflightCheckStatus.Passed);
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
