using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.Application;

/// <summary>Read one exact Docker network name through pinned SSH. No network creation, adoption,
/// removal, address allocation or cached success.</summary>
public interface INasDockerNetworkProbe
{
    Task<DockerNetworkResult> InspectAsync(string networkName, SshHostKey? approvedKey, CancellationToken token);
}
