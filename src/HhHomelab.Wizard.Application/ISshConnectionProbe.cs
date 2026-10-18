using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.Application;

/// <summary>Read-only SSH/SFTP connection evidence for a fixed target and ephemeral credentials.
/// A null key must stop before authentication and return approval evidence. The caller supplies an
/// explicitly approved host/port-bound pin on a subsequent attempt. No journal or NAS mutation.</summary>
public interface ISshConnectionProbe
{
    Task<SshProbeResult> ProbeAsync(SshHostKey? approvedKey, CancellationToken token);
}
