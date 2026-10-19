namespace HhHomelab.Wizard.Domain;

/// <summary>Bounded matches for one exact network name. Available with no IDs proves only absence
/// at observation time; an existing network is never adopted by its name.</summary>
public sealed class DockerNetworkFacts
{
    public const int MaximumMatches = 16;

    public PreflightFactStatus Status { get; }

    public IReadOnlyList<string> NetworkIds { get; }

    public DockerNetworkFacts(PreflightFactStatus status, IEnumerable<string> networkIds)
    {
        ArgumentNullException.ThrowIfNull(networkIds);
        if (status is not (PreflightFactStatus.Available or PreflightFactStatus.Unavailable))
            throw new ArgumentException("network.status.invalid");

        var ids = networkIds.Take(MaximumMatches + 1).ToArray();
        if (ids.Length > MaximumMatches || ids.Any(id => !IsNetworkId(id)))
            throw new ArgumentException("network.ids.invalid");
        if (ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ids.Length)
            throw new ArgumentException("network.ids.duplicate");
        if (status == PreflightFactStatus.Unavailable && ids.Length != 0)
            throw new ArgumentException("network.ids.incomplete");

        Status = status;
        NetworkIds = Array.AsReadOnly(ids.Select(id => id.ToLowerInvariant()).Order(StringComparer.Ordinal).ToArray());
    }

    private static bool IsNetworkId(string? id)
    {
        // Local networks use SHA-256 IDs; Swarm networks use 25-character base-36 IDs.
        // Neither form is an ownership or compatibility assertion.
        var swarmId = id is { Length: 25 } && id.All(char.IsAsciiLetterOrDigit);
        return ResourceIdentity.IsFingerprint(id) || swarmId;
    }
}

/// <summary>Immutable read-only observation bound to a name, endpoint, approved key, principal and
/// profile. It is not network ownership, a lease, or permission to create or modify a network.</summary>
public sealed class DockerNetworkSnapshot
{
    public string NetworkName { get; }

    public SshHostKey HostKey { get; }

    public string PrincipalFingerprint { get; }

    public string ProfileFingerprint { get; }

    public DateTimeOffset ObservedAt { get; }

    public DockerNetworkFacts Facts { get; }

    public string EnvironmentFingerprint { get; }

    public DockerNetworkSnapshot(SshConnectionTarget target, string networkName, SshHostKey hostKey,
        string profileFingerprint, DateTimeOffset observedAt, DockerNetworkFacts facts)
    {
        if (target is not { IsValid: true } || !IsValidName(networkName))
            throw new ArgumentException("network.request.invalid");
        if (hostKey is not { IsValid: true } || !ResourceIdentity.IsFingerprint(profileFingerprint))
            throw new ArgumentException("network.binding.invalid");
        if (hostKey.EndpointFingerprint != target.EndpointFingerprint)
            throw new ArgumentException("network.endpoint.invalid");
        ArgumentNullException.ThrowIfNull(facts);

        NetworkName = networkName;
        HostKey = hostKey;
        PrincipalFingerprint = NasPreflightSnapshot.PrincipalIdentity(target);
        ProfileFingerprint = profileFingerprint;
        ObservedAt = observedAt.ToUniversalTime();
        Facts = facts;
        EnvironmentFingerprint = ConfigurationRequest.Fingerprint(new
        {
            NetworkName, HostKey, PrincipalFingerprint, ProfileFingerprint, facts.Status, facts.NetworkIds,
        });
    }

    public static bool IsValidName(string? name)
    {
        if (name is not { Length: > 0 and <= 63 })
            return false;
        if (!char.IsAsciiLetterOrDigit(name[0]))
            return false;

        return name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
    }

    public bool IsBoundTo(SshConnectionTarget target, string networkName, SshHostKey pin, string profile)
    {
        if (target is not { IsValid: true })
            return false;

        var destinationMatches = NetworkName == networkName && HostKey == pin;
        var principalMatches = PrincipalFingerprint == NasPreflightSnapshot.PrincipalIdentity(target);
        var contextMatches = principalMatches && ProfileFingerprint == profile;
        return destinationMatches && contextMatches;
    }

    public override string ToString() => "network.snapshot";
}

public sealed record DockerNetworkResult
{
    public SshProbeStatus Status { get; }

    public SshHostKey? HostKey { get; }

    public DockerNetworkSnapshot? Snapshot { get; }

    private DockerNetworkResult(SshProbeStatus status, SshHostKey? hostKey, DockerNetworkSnapshot? snapshot)
    {
        Status = status;
        HostKey = hostKey;
        Snapshot = snapshot;
    }

    public static DockerNetworkResult Observed(DockerNetworkSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new(SshProbeStatus.Ready, snapshot.HostKey, snapshot);
    }

    public static DockerNetworkResult Failed(SshProbeResult failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (failure.Status == SshProbeStatus.Ready)
            throw new ArgumentException("network.failure.invalid");

        return new(failure.Status, failure.HostKey, null);
    }
}
