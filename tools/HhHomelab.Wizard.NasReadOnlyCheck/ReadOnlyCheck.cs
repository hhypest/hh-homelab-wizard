using System.Globalization;
using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;

namespace HhHomelab.Wizard.NasReadOnlyCheck;

/// <summary>Runs the production read-only probes against one NAS in a fixed order and collects a report
/// without secrets, host names, user names or container IDs. Every probe only reads: the SSH identity, sudo
/// listing and SFTP attributes of the home directory, the host preflight, the chosen paths with the
/// ownership marker, the exact Docker network name and the Compose project of a new installation.</summary>
public sealed class ReadOnlyCheck(CheckOptions options, SshProbeCredentials credentials, TextWriter output)
{
    /// <summary>Exit code when the server key still has to be compared and approved.</summary>
    public const int HostKeyApprovalRequired = 3;

    private readonly Dictionary<string, object?> report = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, object?> Report => report;

    public async Task<int> RunAsync(CancellationToken token)
    {
        report["schema"] = 1;
        report["endpoint"] = options.Target.EndpointFingerprint;
        report["profile"] = PinnedRuntimeProfile.Fingerprint;
        report["started"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        var pin = await ConnectAsync(token);
        if (pin is null)
            return options.ApprovedKey is null ? HostKeyApprovalRequired : 1;

        var preflight = await PreflightAsync(pin, token);
        if (!preflight)
            return 1;
        if (options.Layout is null)
            return 0;

        var paths = await PathsAsync(options.Layout, pin, token);
        if (!paths || options.NetworkName is null)
            return paths ? 0 : 1;

        var network = await NetworkAsync(options.NetworkName, pin, token);
        var compose = await ComposeAsync(options.Layout, options.NetworkName, pin, token);
        return network && compose ? 0 : 1;
    }

    private async Task<SshHostKey?> ConnectAsync(CancellationToken token)
    {
        var result = await new SshConnectionProbe(options.Target, credentials).ProbeAsync(options.ApprovedKey, token);
        report["ssh"] = result.Status.ToString();
        if (result.Status == SshProbeStatus.HostKeyApprovalRequired && result.HostKey is { } offered)
        {
            output.WriteLine("ssh: host key approval required");
            output.WriteLine("ssh.host-key: " + offered.Algorithm + " " + offered.Sha256Fingerprint);
            output.WriteLine("Compare it with the key on the NAS, then run again with --host-key \"" + offered.Algorithm + " "
                + offered.Sha256Fingerprint + "\".");
            return null;
        }

        var connected = result.Status is SshProbeStatus.Ready or SshProbeStatus.SftpUnavailable;
        output.WriteLine("ssh: " + result.Status);
        if (!connected)
            return null;

        // SFTP may be switched off in DSM; the remaining checks use SSH commands only.
        if (result.Capabilities is { } capabilities)
        {
            report["ssh.uid"] = capabilities.UserId;
            report["ssh.sudo-list-without-prompt"] = capabilities.CanListSudoWithoutPrompt;
            output.WriteLine("ssh.uid: " + capabilities.UserId);
            output.WriteLine("ssh.sudo-list-without-prompt: " + capabilities.CanListSudoWithoutPrompt);
        }

        return options.ApprovedKey;
    }

    private async Task<bool> PreflightAsync(SshHostKey pin, CancellationToken token)
    {
        var result = await new NasPreflightProbe(options.Target, credentials).CollectAsync(pin, token);
        report["preflight"] = result.Status.ToString();
        output.WriteLine("preflight: " + result.Status);
        if (result.Snapshot is not { } snapshot)
            return false;

        var facts = snapshot.Facts;
        var resources = facts.Resources;
        var summary = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["kernel"] = facts.Kernel,
            ["architecture"] = facts.Architecture,
            ["clock-offset-seconds"] = (long)(facts.ServerTime - snapshot.CompletedAt).TotalSeconds,
            ["dsm"] = Version(facts.Dsm),
            ["container-manager"] = Version(facts.ContainerManager),
            ["docker"] = Version(facts.DockerServer),
            ["compose"] = Version(facts.Compose),
            ["model"] = resources.Model.Status == PreflightFactStatus.Available ? resources.Model.Name : resources.Model.Status.ToString(),
            ["containers"] = facts.ContainerInventoryStatus == PreflightFactStatus.Available
                ? facts.ContainerIds.Count.ToString(CultureInfo.InvariantCulture)
                : facts.ContainerInventoryStatus.ToString(),
            ["listening-tcp-ports"] = resources.ListeningStatus == PreflightFactStatus.Available
                ? string.Join(',', resources.ListeningTcpPorts)
                : resources.ListeningStatus.ToString(),
            ["volumes"] = resources.Volumes.Select(volume => new
            {
                volume.Mount,
                TotalGiB = Math.Round(volume.TotalBytes / 1073741824.0, 1),
                AvailableGiB = Math.Round(volume.AvailableBytes / 1073741824.0, 1),
            }).ToArray(),
            ["checks"] = Checks(NasPreflightAssessment.Evaluate(snapshot)),
        };
        report["preflight.facts"] = summary;
        foreach (var (name, value) in summary)
        {
            if (value is string or long)
                output.WriteLine("preflight." + name + ": " + value);
        }

        foreach (var volume in resources.Volumes)
            output.WriteLine("preflight.volume: " + volume.Mount + " total=" + volume.TotalBytes + " available=" + volume.AvailableBytes);
        PrintChecks("preflight", NasPreflightAssessment.Evaluate(snapshot));
        return true;
    }

    private async Task<bool> PathsAsync(MediaPathLayout layout, SshHostKey pin, CancellationToken token)
    {
        var result = await new NasPathProbe(options.Target, credentials).InspectAsync(layout, pin, token);
        report["paths"] = result.Status.ToString();
        output.WriteLine("paths: " + result.Status);
        if (result.Snapshot is not { } snapshot)
            return false;

        var observations = snapshot.Observations.Select(observation => new
        {
            Role = observation.Role.ToString(),
            Kind = observation.Kind.ToString(),
            observation.AncestorDepth,
            Canonical = observation.Canonical.ToString(),
            observation.OwnerUid,
            observation.OwnerGid,
            Mode = observation.Mode is { } mode ? Convert.ToString(mode, 8) : null,
            Contents = observation.Contents.ToString(),
        }).ToArray();
        report["paths.observations"] = observations;
        report["paths.ownership-marker"] = snapshot.Ownership.State.ToString();
        report["paths.identity"] = snapshot.Identity.UserId + ":" + snapshot.Identity.PrimaryGroupId;
        report["paths.checks"] = Checks(RemotePathAssessment.Evaluate(snapshot));
        foreach (var observation in observations)
        {
            output.WriteLine("paths." + observation.Role + ": " + observation.Kind + " canonical=" + observation.Canonical
                + " owner=" + observation.OwnerUid + ":" + observation.OwnerGid + " mode=" + observation.Mode
                + " contents=" + observation.Contents);
        }

        output.WriteLine("paths.ownership-marker: " + snapshot.Ownership.State);
        output.WriteLine("paths.identity: " + snapshot.Identity.UserId + ":" + snapshot.Identity.PrimaryGroupId);
        PrintChecks("paths", RemotePathAssessment.Evaluate(snapshot));
        return true;
    }

    private async Task<bool> NetworkAsync(string networkName, SshHostKey pin, CancellationToken token)
    {
        var result = await new NasDockerNetworkProbe(options.Target, credentials).InspectAsync(networkName, pin, token);
        report["network"] = result.Status.ToString();
        output.WriteLine("network: " + result.Status);
        if (result.Snapshot is not { } snapshot)
            return false;

        var facts = snapshot.Facts;
        report["network.status"] = facts.Status.ToString();
        report["network.matches"] = facts.NetworkIds.Count;
        output.WriteLine("network.status: " + facts.Status + " matches=" + facts.NetworkIds.Count);
        return true;
    }

    /// <summary>A Compose project of a new installation: its project name cannot exist yet, so any match with
    /// the chosen network or container name is reported as Foreign, never as this wizard's own.</summary>
    private async Task<bool> ComposeAsync(MediaPathLayout layout, string networkName, SshHostKey pin, CancellationToken token)
    {
        var identity = new ComposeProjectIdentity("hhw-" + Guid.NewGuid().ToString("N"), QbittorrentConfigurationRenderer.NetworkKey,
            networkName, "qbittorrent", "qbittorrent", layout.ConfigurationRoot + "/media");
        var result = await new NasComposeProjectProbe(options.Target, credentials).InspectAsync(identity, pin, token);
        report["compose"] = result.Status.ToString();
        output.WriteLine("compose: " + result.Status);
        if (result.Snapshot is not { } snapshot)
            return false;

        var facts = snapshot.Facts;
        report["compose.state"] = facts.State.ToString();
        report["compose.networks"] = facts.Networks.Count;
        report["compose.containers"] = facts.Containers.Select(container => container.Ownership + ":" + container.State).ToArray();
        output.WriteLine("compose.state: " + facts.State + " networks=" + facts.Networks.Count + " containers="
            + string.Join(',', facts.Containers.Select(container => container.Ownership + ":" + container.State)));
        return true;
    }

    private void PrintChecks(string area, IReadOnlyList<PreflightCheck> checks)
    {
        foreach (var check in checks)
            output.WriteLine(area + ".check: " + check.Code + " " + check.Status);
    }

    private static string[] Checks(IReadOnlyList<PreflightCheck> checks) =>
        checks.Select(check => check.Code + ":" + check.Status).ToArray();

    private static string Version(PreflightVersion version) =>
        version.Status == PreflightFactStatus.Available ? version.Version ?? "" : version.Status.ToString();
}
