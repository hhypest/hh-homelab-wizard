using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.Application;

/// <summary>Read which network and container with the names of one Compose project exist and whether their
/// Compose labels prove this installation's project. Requires an approved host key. No start, stop,
/// removal, pull, adoption by name or cached success.</summary>
public interface INasComposeProjectProbe
{
    Task<ComposeProjectResult> InspectAsync(ComposeProjectIdentity identity, SshHostKey approvedKey, CancellationToken token);
}
