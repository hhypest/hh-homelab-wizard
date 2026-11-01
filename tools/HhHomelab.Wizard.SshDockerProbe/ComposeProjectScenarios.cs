using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using HhHomelab.Wizard.ProbeSupport;

/// <summary>Reads the network and container of a Compose project on the nested daemon through the production
/// probe. The fixture creates labelled resources itself, as Compose would, without running Compose or starting
/// a container; the probe only lists and inspects.</summary>
internal static class ComposeProjectScenarios
{
    private const string Directory = "/volume1/apps/hhw/media";

    internal static async Task RunAsync(OwnedDockerBench bench, SshProbeCredentials credentials, SshHostKey pin,
        CancellationToken token)
    {
        var before = await bench.ObserveSshDockerResourcesAsync(token);
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var identity = Identity(Guid.NewGuid(), "hhw-net-" + suffix, "hhw-qb-" + suffix);
        var absent = await ReadAsync(bench, credentials, pin, identity, token);
        Require(absent.Facts.State == ComposeProjectState.Absent, "absent");

        var networkId = await bench.CreateSshDockerLabelledNetworkAsync(identity.NetworkName, NetworkLabels(identity), token);
        var networkOnly = await ReadAsync(bench, credentials, pin, identity, token);
        Require(networkOnly.Facts.State == ComposeProjectState.Owned, "network.owned");
        Require(networkOnly.Facts.Networks.Single().Id == networkId, "network.id");
        Require(networkOnly.Facts.OwnContainer is null, "network.no-container");

        var containerId = await bench.CreateSshDockerLabelledContainerAsync(identity.ContainerName, ContainerLabels(identity), token);
        var owned = await ReadAsync(bench, credentials, pin, identity, token);
        Require(owned.Facts.State == ComposeProjectState.Owned, "container.owned");
        Require(owned.Facts.OwnContainer is { State: ComposeContainerState.Created } container && container.Id == containerId, "container.created");
        Console.WriteLine("ssh-docker.compose-project-owned: passed");

        await VerifyExactNamesAsync(bench, credentials, pin, identity, token);
        await VerifyOtherInstallationAsync(bench, credentials, pin, identity, token);
        await VerifyOverridesAsync(bench, credentials, pin, identity, owned, token);
        await VerifyDenialAsync(bench, credentials, pin, identity, token);
        var recovered = await ReadAsync(bench, credentials, pin, identity, token);
        Require(recovered.EnvironmentFingerprint == owned.EnvironmentFingerprint, "denial.recovered");

        await bench.RemoveSshDockerLabelledResourceAsync("container", containerId, token);
        await bench.RemoveSshDockerLabelledResourceAsync("network", networkId, token);
        var removed = await ReadAsync(bench, credentials, pin, identity, token);
        Require(removed.Facts.State == ComposeProjectState.Absent, "removed.absent");
        Require(await bench.ObserveSshDockerResourcesAsync(token) == before, "resources.restored");
        Console.WriteLine("ssh-docker.compose-project-read-only: passed");
    }

    /// <summary>Docker's name filter matches substrings; names that only contain the real ones are not matches.</summary>
    private static async Task VerifyExactNamesAsync(OwnedDockerBench bench, SshProbeCredentials credentials, SshHostKey pin,
        ComposeProjectIdentity identity, CancellationToken token)
    {
        var shorter = new ComposeProjectIdentity(identity.ProjectName, identity.NetworkKey, identity.NetworkName[..^1],
            identity.ServiceName, identity.ContainerName[..^1], identity.WorkingDirectory);
        var read = await ReadAsync(bench, credentials, pin, shorter, token);
        Require(read.Facts.State == ComposeProjectState.Absent, "substring.not-a-match");
    }

    /// <summary>The same names under another installation's project are never this installation's resources.</summary>
    private static async Task VerifyOtherInstallationAsync(OwnedDockerBench bench, SshProbeCredentials credentials, SshHostKey pin,
        ComposeProjectIdentity identity, CancellationToken token)
    {
        var other = Identity(Guid.NewGuid(), identity.NetworkName, identity.ContainerName);
        var read = await ReadAsync(bench, credentials, pin, other, token);
        Require(read.Facts.State == ComposeProjectState.Foreign, "other-installation.foreign");
        var bothMatched = read.Facts.Networks.Count == 1 && read.Facts.Containers.Count == 1;
        var networkForeign = read.Facts.Networks.All(match => match.Ownership == ComposeResourceOwnership.Foreign);
        var containerForeign = read.Facts.Containers.All(match => match.Ownership == ComposeResourceOwnership.Foreign);
        Require(bothMatched && networkForeign && containerForeign, "other-installation.labels");
    }

    private static async Task VerifyOverridesAsync(OwnedDockerBench bench, SshProbeCredentials credentials, SshHostKey pin,
        ComposeProjectIdentity identity, ComposeProjectSnapshot expected, CancellationToken token)
    {
        await bench.SetSshFaultAsync(SshFixtureFault.DockerOverrides, token);
        try
        {
            var read = await ReadAsync(bench, credentials, pin, identity, token);
            Require(read.EnvironmentFingerprint == expected.EnvironmentFingerprint, "overrides.local-socket");
        }
        finally
        {
            await bench.SetSshFaultAsync(SshFixtureFault.Normal, token);
        }
    }

    private static async Task VerifyDenialAsync(OwnedDockerBench bench, SshProbeCredentials credentials, SshHostKey pin,
        ComposeProjectIdentity identity, CancellationToken token)
    {
        await bench.SetSshDockerSocketAccessAsync(allowed: false, token);
        try
        {
            var denied = await ReadAsync(bench, credentials, pin, identity, token);
            Require(denied.Facts.State == ComposeProjectState.Unavailable, "denial.not-absence");
            Require(denied.Facts.Networks.Count == 0 && denied.Facts.Containers.Count == 0, "denial.no-partial-evidence");
        }
        finally
        {
            await bench.SetSshDockerSocketAccessAsync(allowed: true, token);
        }
    }

    private static ComposeProjectIdentity Identity(Guid installation, string network, string container) =>
        new("hhw-" + installation.ToString("N"), QbittorrentConfigurationRenderer.NetworkKey, network, container, container, Directory);

    private static Dictionary<string, string> NetworkLabels(ComposeProjectIdentity identity) => new(StringComparer.Ordinal)
    {
        ["com.docker.compose.project"] = identity.ProjectName,
        ["com.docker.compose.network"] = identity.NetworkKey,
    };

    private static Dictionary<string, string> ContainerLabels(ComposeProjectIdentity identity) => new(StringComparer.Ordinal)
    {
        ["com.docker.compose.project"] = identity.ProjectName,
        ["com.docker.compose.service"] = identity.ServiceName,
        ["com.docker.compose.oneoff"] = "False",
        ["com.docker.compose.project.working_dir"] = identity.WorkingDirectory,
    };

    private static async Task<ComposeProjectSnapshot> ReadAsync(OwnedDockerBench bench, SshProbeCredentials credentials,
        SshHostKey pin, ComposeProjectIdentity identity, CancellationToken token)
    {
        var result = await new NasComposeProjectProbe(bench.SshTarget, credentials).InspectAsync(identity, pin, token);
        return result.Snapshot ?? throw new SshScenarioException("compose.read." + result.Status);
    }

    private static void Require(bool satisfied, string code)
    {
        if (!satisfied)
            throw new SshScenarioException("compose." + code);
    }
}
