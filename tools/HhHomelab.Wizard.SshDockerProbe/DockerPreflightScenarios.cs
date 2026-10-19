using System.Security.Cryptography;
using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using HhHomelab.Wizard.ProbeSupport;

internal static class DockerPreflightScenarios
{
    internal static async Task RunAsync(OwnedDockerBench bench, CancellationToken token)
    {
        using var key = FixtureKey.Create();
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await bench.ConfigureSshCredentialsAsync(password, key.PublicKey, token);
        var pin = await bench.ReadSshHostKeyAsync(token);
        using var credentials = SshProbeCredentials.Password(password);
        using var keyCredentials = SshProbeCredentials.PrivateKey(key.PrivateKey, key.Passphrase);

        var initialResources = await bench.ObserveSshDockerResourcesAsync(token);
        var empty = await ReadAsync(bench, credentials, pin, token);
        Available(empty, []);
        var keySnapshot = await ReadAsync(bench, keyCredentials, pin, token);
        Available(keySnapshot, []);
        Require(keySnapshot.EnvironmentFingerprint == empty.EnvironmentFingerprint, "password-key.identity");
        await UnchangedAsync(bench, initialResources, token);
        Console.WriteLine("ssh-docker.empty-password-key: passed");

        await DockerNetworkScenarios.RunAsync(bench, credentials, keyCredentials, pin, token);

        var expected = await VerifyResourceDriftAsync(bench, credentials, pin, empty, token);
        var resources = await bench.ObserveSshDockerResourcesAsync(token);
        await VerifyOverridesAsync(bench, credentials, pin, expected, token);
        await VerifySocketDenialAsync(bench, credentials, pin, expected, token);
        await VerifyComposeDenialAsync(bench, credentials, pin, expected, token);
        await VerifyDaemonRestartAsync(bench, credentials, pin, expected, token);
        await UnchangedAsync(bench, resources, token);
        Console.WriteLine("ssh-docker.read-only-resource-state: passed");
    }

    private static async Task<NasPreflightSnapshot> VerifyResourceDriftAsync(OwnedDockerBench bench,
        SshProbeCredentials credentials, SshHostKey pin, NasPreflightSnapshot empty, CancellationToken token)
    {
        await bench.ImportSshDockerFixtureImageAsync(token);
        var firstId = await bench.CreateSshDockerFixtureResourceAsync(1, token);
        var before = await bench.ObserveSshDockerResourcesAsync(token);
        var one = await ReadAsync(bench, credentials, pin, token);
        Available(one, [firstId]);
        Require(one.EnvironmentFingerprint != empty.EnvironmentFingerprint, "first-resource.drift");
        var checks = NasPreflightAssessment.Evaluate(one);
        Require(checks.Any(check => check.Code == "preflight.containers.ownership-required" && check.Status == PreflightCheckStatus.Blocking), "ownership-required");
        await UnchangedAsync(bench, before, token);

        var secondId = await bench.CreateSshDockerFixtureResourceAsync(2, token);
        before = await bench.ObserveSshDockerResourcesAsync(token);
        var two = await ReadAsync(bench, credentials, pin, token);
        Available(two, [firstId, secondId]);
        Require(two.EnvironmentFingerprint != one.EnvironmentFingerprint, "second-resource.drift");
        Require(one.Facts.ContainerIds.SequenceEqual([firstId]), "prior-snapshot.immutable");
        await UnchangedAsync(bench, before, token);
        Console.WriteLine("ssh-docker.resources-and-drift: passed");
        return two;
    }

    private static async Task VerifyOverridesAsync(OwnedDockerBench bench, SshProbeCredentials credentials,
        SshHostKey pin, NasPreflightSnapshot expected, CancellationToken token)
    {
        await bench.SetSshFaultAsync(SshFixtureFault.DockerOverrides, token);
        try
        {
            var observed = await ReadAsync(bench, credentials, pin, token);
            Available(observed, expected.Facts.ContainerIds);
            Require(observed.EnvironmentFingerprint == expected.EnvironmentFingerprint, "environment-overrides");
        }
        finally
        {
            await bench.SetSshFaultAsync(SshFixtureFault.Normal, token);
        }
        Console.WriteLine("ssh-docker.endpoint-overrides: passed");
    }

    private static async Task VerifySocketDenialAsync(OwnedDockerBench bench, SshProbeCredentials credentials,
        SshHostKey pin, NasPreflightSnapshot expected, CancellationToken token)
    {
        await bench.SetSshDockerSocketAccessAsync(allowed: false, token);
        try
        {
            var denied = await ReadAsync(bench, credentials, pin, token);
            DockerUnavailable(denied);
            Require(denied.Facts.Compose.Status == PreflightFactStatus.Available, "socket-denied.compose-independent");
        }
        finally
        {
            await bench.SetSshDockerSocketAccessAsync(allowed: true, token);
        }
        await RecoveredAsync(bench, credentials, pin, expected, token);
        Console.WriteLine("ssh-docker.socket-denial-and-recovery: passed");
    }

    private static async Task VerifyComposeDenialAsync(OwnedDockerBench bench, SshProbeCredentials credentials,
        SshHostKey pin, NasPreflightSnapshot expected, CancellationToken token)
    {
        await bench.SetSshComposeAccessAsync(allowed: false, token);
        try
        {
            var denied = await ReadAsync(bench, credentials, pin, token);
            Require(denied.Facts.Compose.Status == PreflightFactStatus.Unavailable, "compose-denied");
            Require(denied.Facts.DockerServer.Status == PreflightFactStatus.Available, "compose-denied.docker-independent");
            Require(denied.Facts.ContainerIds.SequenceEqual(expected.Facts.ContainerIds), "compose-denied.inventory");
        }
        finally
        {
            await bench.SetSshComposeAccessAsync(allowed: true, token);
        }
        await RecoveredAsync(bench, credentials, pin, expected, token);
        Console.WriteLine("ssh-docker.compose-denial-and-recovery: passed");
    }

    private static async Task VerifyDaemonRestartAsync(OwnedDockerBench bench, SshProbeCredentials credentials,
        SshHostKey pin, NasPreflightSnapshot expected, CancellationToken token)
    {
        await bench.StopSshDockerDaemonAsync(token);
        try
        {
            var stopped = await ReadAsync(bench, credentials, pin, token);
            DockerUnavailable(stopped);
            Require(stopped.Facts.Compose.Status == PreflightFactStatus.Available, "daemon-stopped.compose-independent");
        }
        finally
        {
            await bench.StartSshDockerDaemonAsync(token);
        }
        await RecoveredAsync(bench, credentials, pin, expected, token);
        Console.WriteLine("ssh-docker.daemon-restart-and-recovery: passed");
    }

    private static async Task RecoveredAsync(OwnedDockerBench bench, SshProbeCredentials credentials,
        SshHostKey pin, NasPreflightSnapshot expected, CancellationToken token)
    {
        var observed = await ReadAsync(bench, credentials, pin, token);
        Available(observed, expected.Facts.ContainerIds);
        Require(observed.EnvironmentFingerprint == expected.EnvironmentFingerprint, "recovered.identity");
    }

    private static async Task<NasPreflightSnapshot> ReadAsync(OwnedDockerBench bench,
        SshProbeCredentials credentials, SshHostKey pin, CancellationToken token)
    {
        var result = await new NasPreflightProbe(bench.SshTarget, credentials).CollectAsync(pin, token);
        Require(result.Status == SshProbeStatus.Ready, "collection." + result.Status);
        return result.Snapshot ?? throw new SshScenarioException("ssh-docker.snapshot.missing");
    }

    private static void Available(NasPreflightSnapshot snapshot, IEnumerable<string> ids)
    {
        var facts = snapshot.Facts;
        Require(facts.DockerServer == new PreflightVersion(PreflightFactStatus.Available, "29.8.2"), "docker-version");
        Require(facts.Compose == new PreflightVersion(PreflightFactStatus.Available, "5.1.4"), "compose-version");
        Require(facts.ContainerInventoryStatus == PreflightFactStatus.Available, "inventory-status");
        Require(facts.ContainerIds.SequenceEqual(ids.Order(StringComparer.Ordinal)), "inventory-ids");
        Require(facts.Dsm.Status == PreflightFactStatus.Missing, "non-dsm");
        var checks = NasPreflightAssessment.Evaluate(snapshot);
        Require(checks.Any(check => check.Code == "preflight.docker-server" && check.Status == PreflightCheckStatus.Unverified), "compatibility-unverified");
        Require(checks.Any(check => check.Status == PreflightCheckStatus.NotChecked), "remaining-checks");
        PreflightScenarios.HostResources(snapshot, "ssh-docker");
    }

    private static void DockerUnavailable(NasPreflightSnapshot snapshot)
    {
        Require(snapshot.Facts.DockerServer.Status == PreflightFactStatus.Unavailable, "docker-unavailable");
        Require(snapshot.Facts.ContainerInventoryStatus == PreflightFactStatus.Unavailable, "inventory-unavailable");
        Require(snapshot.Facts.ContainerIds.Count == 0, "no-partial-ids");
        Require(NasPreflightAssessment.Evaluate(snapshot).Any(check =>
            check.Code == "preflight.containers.unavailable" && check.Status == PreflightCheckStatus.Blocking), "unavailable-blocks");
    }

    private static async Task UnchangedAsync(OwnedDockerBench bench, string expected, CancellationToken token) =>
        Require(await bench.ObserveSshDockerResourcesAsync(token) == expected, "resources-unchanged");

    private static void Require(bool satisfied, string code)
    {
        if (!satisfied)
            throw new SshScenarioException("ssh-docker." + code);
    }
}
