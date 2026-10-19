using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using HhHomelab.Wizard.ProbeSupport;

internal static class PreflightScenarios
{
    internal static async Task RunAsync(OwnedDockerBench bench, SshProbeCredentials credentials,
        SshProbeCredentials keyCredentials, SshHostKey pin, CancellationToken token)
    {
        using var wrong = SshProbeCredentials.Password("synthetic-preflight-wrong-password");
        var first = await new NasPreflightProbe(bench.SshTarget, wrong).CollectAsync(null, token);
        Expect(first, SshProbeStatus.HostKeyApprovalRequired);
        Require(first.HostKey == pin && first.Snapshot is null, "preflight.first-use.evidence");
        Expect(await new NasPreflightProbe(bench.SshTarget, wrong).CollectAsync(pin, token), SshProbeStatus.AuthenticationRequired);
        var rotated = SshHostKey.FromPublicKey(bench.SshTarget.EndpointFingerprint, "ssh-ed25519", [1, 2, 3]);
        Expect(await new NasPreflightProbe(bench.SshTarget, credentials).CollectAsync(rotated, token), SshProbeStatus.HostKeyChanged);

        var password = await ObserveAsync(bench, credentials, pin, token);
        var key = await ObserveAsync(bench, keyCredentials, pin, token);
        Require(password.EnvironmentFingerprint == key.EnvironmentFingerprint, "preflight.fresh-environment.identity");
        Require(!ReferenceEquals(password, key), "preflight.fresh-observation.identity");
        Console.WriteLine("ssh-runtime.preflight-read-only-and-trust: passed");
        await VerifyFailuresAsync(bench, credentials, pin, token);
        await ObserveAsync(bench, credentials, pin, token);
        Console.WriteLine("ssh-runtime.preflight-faults-and-recovery: passed");
    }

    private static async Task<NasPreflightSnapshot> ObserveAsync(OwnedDockerBench bench, SshProbeCredentials credentials,
        SshHostKey pin, CancellationToken token)
    {
        var result = await new NasPreflightProbe(bench.SshTarget, credentials).CollectAsync(pin, token);
        Expect(result, SshProbeStatus.Ready);
        var snapshot = result.Snapshot ?? throw new SshScenarioException("preflight.snapshot.missing");
        Require(snapshot.Facts.Kernel == "Linux" && snapshot.Facts.Architecture == "x86_64", "preflight.platform");
        Require(snapshot.Facts.Dsm.Status == PreflightFactStatus.Missing, "preflight.non-dsm");
        Require(snapshot.Facts.ContainerManager.Status == PreflightFactStatus.Missing, "preflight.no-container-manager");
        Require(snapshot.Facts.DockerServer.Status == PreflightFactStatus.Missing, "preflight.no-docker");
        Require(snapshot.Facts.ContainerInventoryStatus == PreflightFactStatus.Unavailable, "preflight.inventory-unavailable");
        Require(snapshot.IsCurrentFor(bench.SshTarget, pin, PinnedRuntimeProfile.Fingerprint, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2)), "preflight.binding");
        var checks = NasPreflightAssessment.Evaluate(snapshot);
        Require(checks.Any(check => check.Status == PreflightCheckStatus.Blocking), "preflight.not-ready");
        Require(checks.Any(check => check.Status == PreflightCheckStatus.NotChecked), "preflight.unchecked");
        HostResources(snapshot, "preflight");
        return snapshot;
    }

    /// <summary>The fixture sshd really listens on 2222 and the container has no DSM volume mounts.</summary>
    internal static void HostResources(NasPreflightSnapshot snapshot, string scope)
    {
        var resources = snapshot.Facts.Resources;
        Require(resources.Model.Status == PreflightFactStatus.Missing, scope + ".non-dsm-model");
        Require(resources.ListeningStatus == PreflightFactStatus.Available, scope + ".listeners");
        Require(resources.ListeningTcpPorts.Contains(FixtureSshPort), scope + ".sshd-listener");
        Require(resources.VolumeStatus == PreflightFactStatus.Available && resources.Volumes.Count == 0, scope + ".no-dsm-volumes");
        var conflicting = new PreflightRequirements([FixtureSshPort, FreePort], "/volume1", 1);
        var checks = NasPreflightAssessment.Evaluate(snapshot, conflicting);
        Require(checks.Any(check => check.Code == "preflight.ports.conflict" && check.Status == PreflightCheckStatus.Blocking), scope + ".port-conflict");
        Require(NasPreflightAssessment.ConflictingPorts(snapshot, conflicting).SequenceEqual([FixtureSshPort]), scope + ".conflicting-ports");
        Require(checks.Any(check => check.Code == "preflight.disk-space.volume-missing" && check.Status == PreflightCheckStatus.Blocking), scope + ".volume-missing");
        var free = NasPreflightAssessment.Evaluate(snapshot, new([FreePort], "/volume1", 1));
        Require(free.Any(check => check.Code == "preflight.ports" && check.Status == PreflightCheckStatus.Passed), scope + ".free-port");
    }

    private const int FixtureSshPort = 2222;

    private const int FreePort = 8123;

    private static async Task VerifyFailuresAsync(OwnedDockerBench bench, SshProbeCredentials credentials,
        SshHostKey pin, CancellationToken token)
    {
        foreach (var fault in new[] { SshFixtureFault.Malformed, SshFixtureFault.Oversized })
        {
            await bench.SetSshFaultAsync(fault, token);
            var result = await new NasPreflightProbe(bench.SshTarget, credentials).CollectAsync(pin, token);
            Expect(result, SshProbeStatus.InvalidResponse);
            Require(result.Snapshot is null, "preflight.partial-result");
        }
        await VerifyCancellationAsync(bench, credentials, pin, token);
        await bench.SetSshFaultAsync(SshFixtureFault.Delay, token);
        var pending = new NasPreflightProbe(bench.SshTarget, credentials, TimeSpan.FromSeconds(3)).CollectAsync(pin, token);
        await bench.WaitForSshCommandAsync(token);
        Expect(await pending.WaitAsync(TimeSpan.FromSeconds(6), token), SshProbeStatus.TimedOut);
        await bench.SetSshFaultAsync(SshFixtureFault.Normal, token);
    }

    private static async Task VerifyCancellationAsync(OwnedDockerBench bench, SshProbeCredentials credentials,
        SshHostKey pin, CancellationToken token)
    {
        await bench.SetSshFaultAsync(SshFixtureFault.Delay, token);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(token);
        var probe = new NasPreflightProbe(bench.SshTarget, credentials);
        var pending = probe.CollectAsync(pin, caller.Token);
        try
        {
            await bench.WaitForSshCommandAsync(token);
            Expect(await probe.CollectAsync(pin, token), SshProbeStatus.Busy);
            caller.Cancel();
            Expect(await pending.WaitAsync(TimeSpan.FromSeconds(6), token), SshProbeStatus.Cancelled);
        }
        finally
        {
            caller.Cancel();
            await pending.WaitAsync(TimeSpan.FromSeconds(6), token);
        }
    }

    private static void Expect(NasPreflightResult result, SshProbeStatus status) =>
        Require(result.Status == status, "preflight." + status + "." + result.Status);

    private static void Require(bool satisfied, string code)
    {
        if (!satisfied)
            throw new SshScenarioException(code);
    }
}
