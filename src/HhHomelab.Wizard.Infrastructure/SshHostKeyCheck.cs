using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.Infrastructure;

// Shared by connection and preflight probes; observing a key never approves it.
internal sealed class SshHostKeyCheck(string endpointFingerprint, SshHostKey? approvedKey)
{
    public bool Accepted { get; private set; }

    public SshProbeResult? Failure { get; private set; }

    public bool Accept(string algorithm, byte[] publicKey)
    {
        Accepted = false;
        if (Failure is not null || publicKey.Length is <= 0 or > 65536)
            return false;
        var observed = SshHostKey.FromPublicKey(endpointFingerprint, algorithm, publicKey);
        if (!observed.IsValid)
            return false;
        if (approvedKey != observed)
        {
            Failure = SshProbeResult.Failed(approvedKey is null
                ? SshProbeStatus.HostKeyApprovalRequired
                : SshProbeStatus.HostKeyChanged, observed);
            return false;
        }
        Accepted = true;
        return true;
    }
}
