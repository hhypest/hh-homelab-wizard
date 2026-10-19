using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using HhHomelab.Wizard.ProbeSupport;

/// <summary>Read-only path inspection through the real OpenSSH server as an unprivileged user.
/// /volume1 is one tmpfs device; downloads/library device mismatch stays a fixture scenario.</summary>
internal static class PathScenarios
{
    internal static async Task RunAsync(OwnedDockerBench bench, SshProbeCredentials credentials, SshHostKey pin, CancellationToken token)
    {
        await bench.PrepareSshPathTreeAsync(token);
        await VerifyLayoutAsync(bench, credentials, pin, token);
        await VerifyBlockingPathsAsync(bench, credentials, pin, token);
        await VerifyInjectionAsync(bench, credentials, pin, token);
        Console.WriteLine("ssh-runtime.paths-read-only: passed");
        await VerifyOwnershipAsync(bench, credentials, pin, token);
        Console.WriteLine("ssh-runtime.ownership-markers: passed");
        await MarkerScenarios.RunAsync(bench, credentials, pin, token);
        await VerifyFaultsAsync(bench, credentials, pin, token);
        Console.WriteLine("ssh-runtime.paths-faults-and-recovery: passed");
    }

    private static async Task VerifyLayoutAsync(OwnedDockerBench bench, SshProbeCredentials credentials, SshHostKey pin, CancellationToken token)
    {
        var snapshot = await InspectAsync(bench, credentials, pin, new("/volume1/data", "/volume1/docker/hh-new"), token);
        var library = snapshot.Observation(RemotePathRole.MediaMovies);
        Require(library is { Kind: RemotePathKind.Directory, Contents: RemotePathContents.NotEmpty }, "paths.existing-library");
        Require(snapshot.Observation(RemotePathRole.TorrentsMovies) is { Kind: RemotePathKind.Missing, AncestorDepth: 2 }, "paths.missing-downloads");
        Require(snapshot.Observation(RemotePathRole.DataRoot).OwnerUid == 0, "paths.owner-observed");
        var checks = RemotePathAssessment.Evaluate(snapshot);
        Require(Has(checks, "paths.media-movies", PreflightCheckStatus.Passed), "paths.library-allowed");
        Require(Has(checks, "ownership.new-installation", PreflightCheckStatus.Passed), "paths.configuration-absent");
        Require(Has(checks, "paths.hardlink-device", PreflightCheckStatus.Passed), "paths.one-device");
        Require(Has(checks, "paths.dsm-acl", PreflightCheckStatus.NotChecked), "paths.acl-not-claimed");
        var again = await InspectAsync(bench, credentials, pin, new("/volume1/data", "/volume1/docker/hh-new"), token);
        Require(again.EnvironmentFingerprint == snapshot.EnvironmentFingerprint, "paths.stable-environment");
    }

    private static async Task VerifyBlockingPathsAsync(OwnedDockerBench bench, SshProbeCredentials credentials, SshHostKey pin, CancellationToken token)
    {
        var redirected = RemotePathAssessment.Evaluate(await InspectAsync(bench, credentials, pin, new("/volume1/link", "/volume1/docker/hh"), token));
        Require(Has(redirected, "paths.data-root.redirected", PreflightCheckStatus.Blocking), "paths.symlink-root");
        Require(Has(redirected, "paths.media-movies.redirected", PreflightCheckStatus.Blocking), "paths.symlink-ancestor");
        Require(Has(redirected, "ownership.unmarked-contents", PreflightCheckStatus.Blocking), "paths.foreign-configuration");

        var hidden = RemotePathAssessment.Evaluate(await InspectAsync(bench, credentials, pin, new("/volume1/locked/data", "/volume1/file/hh"), token));
        Require(Has(hidden, "paths.data-root.unavailable", PreflightCheckStatus.Blocking), "paths.unsearchable-not-missing");
        Require(Has(hidden, "paths.configuration-root.not-directory", PreflightCheckStatus.Blocking), "paths.file-ancestor");

        var absent = RemotePathAssessment.Evaluate(await InspectAsync(bench, credentials, pin, new("/volume2/data", "/volume2/docker"), token));
        Require(Has(absent, "paths.data-root.volume-missing", PreflightCheckStatus.Blocking), "paths.volume-missing");

        var leftover = RemotePathAssessment.Evaluate(await InspectAsync(bench, credentials, pin, new("/volume3/data", "/volume3/docker"), token));
        Require(Has(leftover, "paths.data-root.volume-unmounted", PreflightCheckStatus.Blocking), "paths.leftover-mount-point");
        Require(Has(leftover, "paths.configuration-root.volume-unmounted", PreflightCheckStatus.Blocking), "paths.leftover-ancestor");

        var unlisted = RemotePathAssessment.Evaluate(await InspectAsync(bench, credentials, pin, new("/volume1/lockedleaf", "/volume1/docker/newline"), token));
        Require(Has(unlisted, "paths.data-root.unavailable", PreflightCheckStatus.Blocking), "paths.existing-unsearchable");
        Require(Has(unlisted, "ownership.unmarked-contents", PreflightCheckStatus.Blocking), "paths.newline-name-not-empty");
    }

    private static readonly Guid OwnInstallation = Guid.Parse("4f9e2c1a-7b3d-4e5f-9a8b-1c2d3e4f5a6b");

    private static readonly Guid ForeignInstallation = Guid.Parse("9a8b7c6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d");

    private static async Task VerifyOwnershipAsync(OwnedDockerBench bench, SshProbeCredentials credentials, SshHostKey pin, CancellationToken token)
    {
        var profile = PinnedRuntimeProfile.Fingerprint;
        await bench.WriteSshOwnershipMarkersAsync(Marker(OwnInstallation, profile), Marker(ForeignInstallation, profile), token);
        var owner = new InstallationOwner(OwnInstallation, profile);
        Require(await OwnershipAsync("owned", owner) == "ownership.own-installation", "ownership.own");
        Require(await OwnershipAsync("owned", null) == "ownership.foreign-installation", "ownership.not-adopted");
        Require(await OwnershipAsync("owned", owner with { ProfileFingerprint = new string('c', 64) }) == "ownership.profile-changed", "ownership.profile-changed");
        Require(await OwnershipAsync("foreign", owner) == "ownership.foreign-installation", "ownership.foreign");
        Require(await OwnershipAsync("corrupt", owner) == "ownership.marker-invalid", "ownership.corrupt");
        Require(await OwnershipAsync("linked", owner) == "ownership.marker-redirected", "ownership.symlink");
        Require(await OwnershipAsync("locked", owner) == "ownership.marker-unavailable", "ownership.unsearchable");
        Require(await OwnershipAsync("hh", owner) == "ownership.unmarked-contents", "ownership.unmarked");
        var linked = await InspectAsync(bench, credentials, pin, new("/volume1/data", "/volume1/docker-link/owned"), token);
        Require(InstallationOwnershipAssessment.Evaluate(linked, owner).Code == "ownership.marker-redirected", "ownership.symlinked-ancestor");
        Require(await OwnershipAsync("never-created", owner) == "ownership.new-installation", "ownership.new");

        async Task<string> OwnershipAsync(string configuration, InstallationOwner? expected)
        {
            var snapshot = await InspectAsync(bench, credentials, pin, new("/volume1/data", "/volume1/docker/" + configuration), token);
            return InstallationOwnershipAssessment.Evaluate(snapshot, expected).Code;
        }
    }

    private static string Marker(Guid installation, string profile) =>
        "{\"schema\":1,\"installationId\":\"" + installation.ToString("D") + "\",\"profileFingerprint\":\"" + profile + "\"}";

    private static async Task VerifyInjectionAsync(OwnedDockerBench bench, SshProbeCredentials credentials, SshHostKey pin, CancellationToken token)
    {
        var layout = new MediaPathLayout("/volume1/a;touch hh-paths-injected;$(touch hh-paths-injected)", "/volume1/`touch hh-paths-injected`");
        var snapshot = await InspectAsync(bench, credentials, pin, layout, token);
        Require(snapshot.Observation(RemotePathRole.DataRoot).Kind == RemotePathKind.Missing, "paths.literal-path");
        Require(!await bench.SshHomeChangedAsync(token), "paths.not-evaluated");
    }

    private static async Task VerifyFaultsAsync(OwnedDockerBench bench, SshProbeCredentials credentials, SshHostKey pin, CancellationToken token)
    {
        var layout = new MediaPathLayout("/volume1/data", "/volume1/docker/hh-new");
        await bench.SetSshFaultAsync(SshFixtureFault.Malformed, token);
        var malformed = await new NasPathProbe(bench.SshTarget, credentials).InspectAsync(layout, pin, token);
        Require(malformed.Status == SshProbeStatus.InvalidResponse && malformed.Snapshot is null, "paths.malformed." + malformed.Status);
        await bench.SetSshFaultAsync(SshFixtureFault.Delay, token);
        var pending = new NasPathProbe(bench.SshTarget, credentials, TimeSpan.FromSeconds(3)).InspectAsync(layout, pin, token);
        await bench.WaitForSshCommandAsync(token);
        var timedOut = await pending.WaitAsync(TimeSpan.FromSeconds(6), token);
        Require(timedOut.Status == SshProbeStatus.TimedOut && timedOut.Snapshot is null, "paths.timeout." + timedOut.Status);
        await bench.SetSshFaultAsync(SshFixtureFault.Normal, token);
        await InspectAsync(bench, credentials, pin, layout, token);
    }

    private static async Task<RemotePathSnapshot> InspectAsync(OwnedDockerBench bench, SshProbeCredentials credentials,
        SshHostKey pin, MediaPathLayout layout, CancellationToken token)
    {
        var result = await new NasPathProbe(bench.SshTarget, credentials).InspectAsync(layout, pin, token);
        Require(result.Status == SshProbeStatus.Ready, "paths.collection." + result.Status);
        return result.Snapshot ?? throw new SshScenarioException("paths.snapshot.missing");
    }

    private static bool Has(IEnumerable<PreflightCheck> checks, string code, PreflightCheckStatus status) =>
        checks.Any(check => check.Code == code && check.Status == status);

    private static void Require(bool satisfied, string code)
    {
        if (!satisfied)
            throw new SshScenarioException("ssh-runtime." + code);
    }
}
