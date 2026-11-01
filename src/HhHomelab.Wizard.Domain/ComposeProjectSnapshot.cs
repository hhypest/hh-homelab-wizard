namespace HhHomelab.Wizard.Domain;

/// <summary>What a Compose project of one installation would label its resources with: the project name
/// unique to the installation, the network key and name, the service and container name and the project
/// directory under the marked configuration root. Values travel on stdin and are compared on the NAS; they
/// never become part of a command line.</summary>
public sealed class ComposeProjectIdentity
{
    public const int MaximumDockerNameLength = 63;

    public ComposeProjectIdentity(string projectName, string networkKey, string networkName, string serviceName,
        string containerName, string workingDirectory)
    {
        if (!IsProjectName(projectName))
            throw new ArgumentException("compose.identity.project");
        if (!IsDockerName(networkKey) || !DockerNetworkSnapshot.IsValidName(networkName))
            throw new ArgumentException("compose.identity.network");
        if (!IsDockerName(serviceName) || !IsDockerName(containerName))
            throw new ArgumentException("compose.identity.container");
        if (!MediaPathLayout.IsVolumePath(workingDirectory))
            throw new ArgumentException("compose.identity.directory");

        ProjectName = projectName;
        NetworkKey = networkKey;
        NetworkName = networkName;
        ServiceName = serviceName;
        ContainerName = containerName;
        WorkingDirectory = workingDirectory;
    }

    public string ProjectName { get; }

    /// <summary>The key under `networks:` in the Compose file; Compose stores it as a network label.</summary>
    public string NetworkKey { get; }

    public string NetworkName { get; }

    public string ServiceName { get; }

    public string ContainerName { get; }

    /// <summary>Absolute project directory; Compose records it on every container it creates.</summary>
    public string WorkingDirectory { get; }

    /// <summary>"hhw-" and the 32 lowercase hex digits of an installation ID, as the renderer writes it.</summary>
    public static bool IsProjectName(string? name)
    {
        if (name is not { Length: 36 } || !name.StartsWith("hhw-", StringComparison.Ordinal))
            return false;

        return name[4..].All(character => char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');
    }

    /// <summary>A Docker object name a Go template can compare without quoting: 1–63 ASCII letters, digits,
    /// underscore, dot or hyphen, starting with a letter or digit.</summary>
    public static bool IsDockerName(string? name)
    {
        if (name is not { Length: > 0 and <= MaximumDockerNameLength })
            return false;
        if (!char.IsAsciiLetterOrDigit(name[0]))
            return false;

        return name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-');
    }
}

/// <summary>Whether all Compose labels of a resource name exactly this installation's project; anything
/// else, including missing labels, is Foreign.</summary>
public enum ComposeResourceOwnership
{
    Own,
    Foreign,
}

/// <summary>Docker's container status values; any other value rejects the observation.</summary>
public enum ComposeContainerState
{
    Created,
    Running,
    Paused,
    Restarting,
    Removing,
    Exited,
    Dead,
}

public sealed record ComposeNetworkMatch(string Id, ComposeResourceOwnership Ownership);

public sealed record ComposeContainerMatch(string Id, ComposeResourceOwnership Ownership, ComposeContainerState State);

/// <summary>Conservative reading of the matches; only Absent and Owned may ever lead to a later write.</summary>
public enum ComposeProjectState
{
    /// <summary>No network and no container with these names.</summary>
    Absent,

    /// <summary>Exactly one own network and at most one own container, nothing foreign.</summary>
    Owned,

    /// <summary>Any network or container with these names that this project's labels do not prove.</summary>
    Foreign,

    /// <summary>Only own resources, but not one consistent project, such as a container without its network.</summary>
    Ambiguous,

    /// <summary>Docker could not be read completely; never treated as absence.</summary>
    Unavailable,
}

/// <summary>Bounded exact-name matches for the network and the container of one Compose project.
/// Nothing is adopted by a name: ownership comes only from the Compose labels.</summary>
public sealed class ComposeProjectFacts
{
    public const int MaximumMatches = 16;

    public ComposeProjectFacts(PreflightFactStatus status, IEnumerable<ComposeNetworkMatch> networks,
        IEnumerable<ComposeContainerMatch> containers)
    {
        ArgumentNullException.ThrowIfNull(networks);
        ArgumentNullException.ThrowIfNull(containers);
        if (status is not (PreflightFactStatus.Available or PreflightFactStatus.Unavailable))
            throw new ArgumentException("compose.status.invalid");

        var networkMatches = networks.Take(MaximumMatches + 1).ToArray();
        var containerMatches = containers.Take(MaximumMatches + 1).ToArray();
        ValidateNetworks(networkMatches);
        ValidateContainers(containerMatches);
        var partial = networkMatches.Length != 0 || containerMatches.Length != 0;
        if (status == PreflightFactStatus.Unavailable && partial)
            throw new ArgumentException("compose.matches.incomplete");

        Status = status;
        Networks = Array.AsReadOnly(networkMatches
            .Select(match => match with { Id = match.Id.ToLowerInvariant() })
            .OrderBy(match => match.Id, StringComparer.Ordinal)
            .ToArray());
        Containers = Array.AsReadOnly(containerMatches
            .Select(match => match with { Id = match.Id.ToLowerInvariant() })
            .OrderBy(match => match.Id, StringComparer.Ordinal)
            .ToArray());
        State = Assess();
    }

    public PreflightFactStatus Status { get; }

    public IReadOnlyList<ComposeNetworkMatch> Networks { get; }

    public IReadOnlyList<ComposeContainerMatch> Containers { get; }

    public ComposeProjectState State { get; }

    /// <summary>The own container, only when the project is Owned.</summary>
    public ComposeContainerMatch? OwnContainer => State == ComposeProjectState.Owned ? Containers.SingleOrDefault() : null;

    private ComposeProjectState Assess()
    {
        if (Status == PreflightFactStatus.Unavailable)
            return ComposeProjectState.Unavailable;

        var anyForeignNetwork = Networks.Any(match => match.Ownership == ComposeResourceOwnership.Foreign);
        var anyForeignContainer = Containers.Any(match => match.Ownership == ComposeResourceOwnership.Foreign);
        if (anyForeignNetwork || anyForeignContainer)
            return ComposeProjectState.Foreign;
        if (Networks.Count == 0 && Containers.Count == 0)
            return ComposeProjectState.Absent;

        var oneNetwork = Networks.Count == 1;
        var atMostOneContainer = Containers.Count <= 1;
        if (oneNetwork && atMostOneContainer)
            return ComposeProjectState.Owned;

        return ComposeProjectState.Ambiguous;
    }

    private static void ValidateNetworks(ComposeNetworkMatch[] matches)
    {
        if (matches.Length > MaximumMatches)
            throw new ArgumentException("compose.networks.invalid");
        foreach (var match in matches)
        {
            if (match is null || !DockerNetworkFacts.IsNetworkId(match.Id) || !Enum.IsDefined(match.Ownership))
                throw new ArgumentException("compose.networks.invalid");
        }

        if (matches.Select(match => match.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != matches.Length)
            throw new ArgumentException("compose.networks.duplicate");
    }

    private static void ValidateContainers(ComposeContainerMatch[] matches)
    {
        if (matches.Length > MaximumMatches)
            throw new ArgumentException("compose.containers.invalid");
        foreach (var match in matches)
        {
            if (match is null || !ResourceIdentity.IsFingerprint(match.Id))
                throw new ArgumentException("compose.containers.invalid");
            if (!Enum.IsDefined(match.Ownership) || !Enum.IsDefined(match.State))
                throw new ArgumentException("compose.containers.invalid");
        }

        if (matches.Select(match => match.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != matches.Length)
            throw new ArgumentException("compose.containers.duplicate");
    }
}

/// <summary>Immutable read-only observation bound to the project identity, endpoint, approved key, principal
/// and profile. Not ownership of the NAS, a lease or permission to start, stop or remove anything.</summary>
public sealed class ComposeProjectSnapshot
{
    public ComposeProjectSnapshot(SshConnectionTarget target, ComposeProjectIdentity identity, SshHostKey hostKey,
        string profileFingerprint, DateTimeOffset observedAt, ComposeProjectFacts facts)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(facts);
        if (target is not { IsValid: true })
            throw new ArgumentException("compose.target.invalid");
        if (hostKey is not { IsValid: true } || !ResourceIdentity.IsFingerprint(profileFingerprint))
            throw new ArgumentException("compose.binding.invalid");
        if (hostKey.EndpointFingerprint != target.EndpointFingerprint)
            throw new ArgumentException("compose.endpoint.invalid");

        Identity = identity;
        HostKey = hostKey;
        PrincipalFingerprint = NasPreflightSnapshot.PrincipalIdentity(target);
        ProfileFingerprint = profileFingerprint;
        ObservedAt = observedAt.ToUniversalTime();
        Facts = facts;
        EnvironmentFingerprint = ConfigurationRequest.Fingerprint(new
        {
            identity.ProjectName,
            identity.NetworkKey,
            identity.NetworkName,
            identity.ServiceName,
            identity.ContainerName,
            identity.WorkingDirectory,
            HostKey,
            PrincipalFingerprint,
            ProfileFingerprint,
            facts.Status,
            Networks = facts.Networks.Select(match => match.Id + ":" + match.Ownership),
            Containers = facts.Containers.Select(match => match.Id + ":" + match.Ownership + ":" + match.State),
        });
    }

    public ComposeProjectIdentity Identity { get; }

    public SshHostKey HostKey { get; }

    public string PrincipalFingerprint { get; }

    public string ProfileFingerprint { get; }

    public DateTimeOffset ObservedAt { get; }

    public ComposeProjectFacts Facts { get; }

    /// <summary>Excludes the time, so repeated identical reads compare equal and any change is drift.</summary>
    public string EnvironmentFingerprint { get; }

    public override string ToString() => "compose.snapshot";
}

public sealed record ComposeProjectResult
{
    private ComposeProjectResult(SshProbeStatus status, SshHostKey? hostKey, ComposeProjectSnapshot? snapshot)
    {
        Status = status;
        HostKey = hostKey;
        Snapshot = snapshot;
    }

    public SshProbeStatus Status { get; }

    public SshHostKey? HostKey { get; }

    public ComposeProjectSnapshot? Snapshot { get; }

    public static ComposeProjectResult Observed(ComposeProjectSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new(SshProbeStatus.Ready, snapshot.HostKey, snapshot);
    }

    public static ComposeProjectResult Failed(SshProbeResult failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (failure.Status == SshProbeStatus.Ready)
            throw new ArgumentException("compose.failure.invalid");

        return new(failure.Status, failure.HostKey, null);
    }
}
