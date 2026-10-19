using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.Application;

/// <summary>Observe the chosen media layout on a pinned SSH target without creating, changing
/// permissions of or adopting any directory. A result is not permission to Apply.</summary>
public interface INasPathProbe
{
    Task<RemotePathResult> InspectAsync(MediaPathLayout layout, SshHostKey? approvedKey, CancellationToken token);
}
