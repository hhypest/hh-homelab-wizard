using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using HhHomelab.Wizard.ProbeSupport;

/// <summary>Only reads the nested daemon's networks; host is an existing built-in network even
/// when its default bridge is disabled. No network is created or adopted for this probe.</summary>
internal static class DockerNetworkScenarios
{
    internal static async Task RunAsync(OwnedDockerBench bench, SshProbeCredentials credentials,
        SshProbeCredentials keyCredentials, SshHostKey pin, CancellationToken token)
    {
        var before = await bench.ObserveSshDockerResourcesAsync(token);
        var existing = await ReadAsync(bench, credentials, pin, "host", token);
        Require(existing.Facts.Status == PreflightFactStatus.Available, "host.available");
        Require(existing.Facts.NetworkIds.Count == 1, "host.exact-match");
        var withKey = await ReadAsync(bench, keyCredentials, pin, "host", token);
        Require(withKey.EnvironmentFingerprint == existing.EnvironmentFingerprint, "password-key.same-evidence");
        var missing = await ReadAsync(bench, credentials, pin, "hhw-absent-" + Guid.NewGuid().ToString("N"), token);
        Require(missing.Facts.Status == PreflightFactStatus.Available && missing.Facts.NetworkIds.Count == 0, "absent.proven");
        var substring = await ReadAsync(bench, credentials, pin, "hos", token);
        Require(substring.Facts.Status == PreflightFactStatus.Available && substring.Facts.NetworkIds.Count == 0, "substring.not-a-match");

        await VerifyOverridesAsync(bench, credentials, pin, existing, token);
        await VerifyDenialAsync(bench, credentials, pin, token);
        var recovered = await ReadAsync(bench, credentials, pin, "host", token);
        Require(recovered.EnvironmentFingerprint == existing.EnvironmentFingerprint, "denial.recovered");
        Require(await bench.ObserveSshDockerResourcesAsync(token) == before, "resources.unchanged");
        Console.WriteLine("ssh-docker.network-name-read-only: passed");
    }

    private static async Task VerifyOverridesAsync(OwnedDockerBench bench, SshProbeCredentials credentials,
        SshHostKey pin, DockerNetworkSnapshot expected, CancellationToken token)
    {
        await bench.SetSshFaultAsync(SshFixtureFault.DockerOverrides, token);
        try
        {
            var observed = await ReadAsync(bench, credentials, pin, "host", token);
            Require(observed.EnvironmentFingerprint == expected.EnvironmentFingerprint, "overrides.local-socket");
        }
        finally
        {
            await bench.SetSshFaultAsync(SshFixtureFault.Normal, token);
        }
    }

    private static async Task VerifyDenialAsync(OwnedDockerBench bench, SshProbeCredentials credentials,
        SshHostKey pin, CancellationToken token)
    {
        await bench.SetSshDockerSocketAccessAsync(allowed: false, token);
        try
        {
            var denied = await ReadAsync(bench, credentials, pin, "host", token);
            Require(denied.Facts.Status == PreflightFactStatus.Unavailable, "denial.not-absence");
            Require(denied.Facts.NetworkIds.Count == 0, "denial.no-partial-evidence");
        }
        finally
        {
            await bench.SetSshDockerSocketAccessAsync(allowed: true, token);
        }
    }

    private static async Task<DockerNetworkSnapshot> ReadAsync(OwnedDockerBench bench, SshProbeCredentials credentials,
        SshHostKey pin, string name, CancellationToken token)
    {
        var result = await new NasDockerNetworkProbe(bench.SshTarget, credentials).InspectAsync(name, pin, token);
        return result.Snapshot ?? throw new SshScenarioException("network.read." + result.Status);
    }

    private static void Require(bool satisfied, string code)
    {
        if (!satisfied)
            throw new SshScenarioException("network." + code);
    }
}
