using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.Application;

/// <summary>Collect fresh read-only prerequisite evidence from a pinned SSH target. No arbitrary
/// commands, elevation, writes, installation permission, resource adoption or cached results.</summary>
public interface INasPreflightProbe
{
    Task<NasPreflightResult> CollectAsync(SshHostKey? approvedKey, CancellationToken token);
}
