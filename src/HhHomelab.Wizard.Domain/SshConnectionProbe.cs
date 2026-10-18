using System.Net;
using System.Security.Cryptography;

namespace HhHomelab.Wizard.Domain;

public sealed record SshConnectionTarget(string Host, int Port, string Username)
{
    public bool IsValid => ValidHost(Host)
        && Port is >= 1 and <= 65535
        && Username is { Length: > 0 and <= 128 }
        && !Username.Any(char.IsControl);

    public string EndpointFingerprint
    {
        get
        {
            var canonical = IPAddress.TryParse(Host, out var address)
                ? address.ToString()
                : Host.TrimEnd('.').ToLowerInvariant();
            return ConfigurationRequest.Fingerprint(new { Host = canonical, Port });
        }
    }

    private static bool ValidHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253)
            return false;
        if (host != host.Trim() || host.Any(char.IsControl))
            return false;
        return Uri.CheckHostName(host) != UriHostNameType.Unknown;
    }

    public override string ToString() => "ssh.target";
}

/// <summary>Public server key evidence, bound to a canonical host/port. Never private key material.
/// First-use evidence must be explicitly approved out of band; observing it is not trust.</summary>
public sealed record SshHostKey(string EndpointFingerprint, string Algorithm, string Sha256Fingerprint)
{
    public bool IsValid => ResourceIdentity.IsFingerprint(EndpointFingerprint)
        && ValidAlgorithm(Algorithm)
        && ValidFingerprint(Sha256Fingerprint);

    public static SshHostKey FromPublicKey(string endpointFingerprint, string algorithm, ReadOnlySpan<byte> publicKey)
    {
        var hash = SHA256.HashData(publicKey);
        return new(endpointFingerprint, algorithm, "SHA256:" + Convert.ToBase64String(hash).TrimEnd('='));
    }

    private static bool ValidAlgorithm(string? algorithm) => algorithm is { Length: > 0 and <= 128 }
        && algorithm.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '@');

    private static bool ValidFingerprint(string? fingerprint)
    {
        if (fingerprint is not { Length: 50 } || !fingerprint.StartsWith("SHA256:", StringComparison.Ordinal))
            return false;
        Span<byte> bytes = stackalloc byte[32];
        return Convert.TryFromBase64String(fingerprint[7..] + "=", bytes, out var count) && count == 32;
    }
}

public enum SshProbeStatus
{
    Ready,
    HostKeyApprovalRequired,
    HostKeyChanged,
    AuthenticationRequired,
    TimedOut,
    Cancelled,
    TransportFailure,
    InvalidInput,
    InvalidResponse,
    SftpUnavailable,
    Busy,
}

/// <summary>Read-only transport evidence. Sudo listing availability does not prove that installation
/// commands are authorized. Linux/UID/SFTP do not prove DSM compatibility or resource ownership.</summary>
public sealed record SshHostCapabilities(int UserId, bool CanListSudoWithoutPrompt);

public sealed record SshProbeResult
{
    public SshProbeStatus Status { get; }

    public SshHostKey? HostKey { get; }

    public SshHostCapabilities? Capabilities { get; }

    private SshProbeResult(SshProbeStatus status, SshHostKey? hostKey, SshHostCapabilities? capabilities)
    {
        Status = status;
        HostKey = hostKey;
        Capabilities = capabilities;
    }

    public static SshProbeResult Ready(SshHostKey key, SshHostCapabilities capabilities)
    {
        if (key is not { IsValid: true } || capabilities is not { UserId: >= 0 })
            throw new ArgumentException("ssh.probe.invalid-evidence");
        return new(SshProbeStatus.Ready, key, capabilities);
    }

    public static SshProbeResult Failed(SshProbeStatus status, SshHostKey? key = null)
    {
        if (!Enum.IsDefined(status) || status == SshProbeStatus.Ready)
            throw new ArgumentException("ssh.probe.invalid-status");
        if (key is not null && !key.IsValid)
            throw new ArgumentException("ssh.probe.invalid-key");
        var needsKey = status is SshProbeStatus.HostKeyApprovalRequired or SshProbeStatus.HostKeyChanged;
        if (needsKey && key is null)
            throw new ArgumentException("ssh.probe.missing-key");
        return new(status, key, null);
    }
}
