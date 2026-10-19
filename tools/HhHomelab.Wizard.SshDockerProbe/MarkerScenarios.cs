using HhHomelab.Wizard.Application;
using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using HhHomelab.Wizard.ProbeSupport;

/// <summary>The first journaled NAS mutation through the real OpenSSH server as an unprivileged user:
/// a local file journal, the shared operation scenario and a fixed marker script. Only the disposable
/// fixture tree is written; the journal lives in a fresh temporary directory of the runner.</summary>
internal static class MarkerScenarios
{
    internal static async Task RunAsync(OwnedDockerBench bench, SshProbeCredentials credentials, SshHostKey pin, CancellationToken token)
    {
        var foreign = "{\"schema\":1,\"installationId\":\"" + Guid.NewGuid().ToString("D")
            + "\",\"profileFingerprint\":\"" + PinnedRuntimeProfile.Fingerprint + "\"}";
        await bench.PrepareSshMarkerWriteTreeAsync(foreign, token);
        var directory = Path.Combine(Path.GetTempPath(), $"wizard-marker-stand-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var context = new Context(bench, credentials, pin, directory);
            await VerifyCreateAndResumeAsync(context, token);
            await VerifyLostResponseAsync(context, token);
            await VerifyRefusalsAsync(context, token);
            Console.WriteLine("ssh-runtime.marker-journaled-write: passed");
        }
        finally
        {
            await bench.SetSshFaultAsync(SshFixtureFault.Normal, token);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task VerifyCreateAndResumeAsync(Context context, CancellationToken token)
    {
        var request = context.NewRequest("/volume1/apps/created");
        Expect(await context.RunAsync(request, token), OperationOutcomeKind.Applied, "marker.created");
        Require(await context.OwnershipAsync(request, token) == "ownership.own-installation", "marker.observed-own");
        Expect(await context.RunAsync(request, token), OperationOutcomeKind.AlreadySatisfied, "marker.resume-observes");

        var staged = context.NewRequest("/volume1/apps/staged");
        Require(await context.OwnershipAsync(staged, token) == "ownership.new-installation", "marker.staged-is-new");
        Expect(await context.RunAsync(staged, token), OperationOutcomeKind.Applied, "marker.staged-completed");
    }

    /// <summary>The script really writes but the response is withheld: Unknown, then a new run with the
    /// same journal confirms by observation; the marker directory holds exactly one marker.</summary>
    private static async Task VerifyLostResponseAsync(Context context, CancellationToken token)
    {
        var request = context.NewRequest("/volume1/apps/lost");
        await context.Bench.SetSshFaultAsync(SshFixtureFault.MarkerLostResponse, token);
        var lost = await context.RunAsync(request, token, TimeSpan.FromSeconds(3));
        Require(lost.Kind == OperationOutcomeKind.Unknown && lost.Code == OperationErrorCode.WriteUncertain, "marker.lost." + lost.Kind);
        await context.Bench.SetSshFaultAsync(SshFixtureFault.Normal, token);
        Expect(await context.RunAsync(request, token), OperationOutcomeKind.Applied, "marker.lost-resumed");
        Expect(await context.RunAsync(request, token), OperationOutcomeKind.AlreadySatisfied, "marker.lost-settled");
    }

    private static async Task VerifyRefusalsAsync(Context context, CancellationToken token)
    {
        var foreign = context.NewRequest("/volume1/docker/foreign");
        Expect(await context.RunAsync(foreign, token), OperationOutcomeKind.Conflict, "marker.foreign-untouched");
        Require(await context.OwnershipAsync(foreign, token) == "ownership.foreign-installation", "marker.foreign-unchanged");
        var unmarked = context.NewRequest("/volume1/docker/hh");
        Expect(await context.RunAsync(unmarked, token), OperationOutcomeKind.Conflict, "marker.unmarked-untouched");
        var foreignStaged = context.NewRequest("/volume1/apps/foreign-staged");
        Require(await context.OwnershipAsync(foreignStaged, token) == "ownership.foreign-installation", "marker.foreign-staged-not-new");
        Expect(await context.RunAsync(foreignStaged, token), OperationOutcomeKind.Conflict, "marker.foreign-staged-untouched");
        var linkedTemporary = context.NewRequest("/volume1/apps/linked-tmp");
        Require(await context.OwnershipAsync(linkedTemporary, token) == "ownership.unmarked-contents", "marker.symlink-tmp-not-staged");
        Expect(await context.RunAsync(linkedTemporary, token), OperationOutcomeKind.Conflict, "marker.symlink-tmp-untouched");
        var unwritable = context.NewRequest("/volume1/readonly/hh");
        var refused = await context.RunAsync(unwritable, token);
        Require(refused.Kind == OperationOutcomeKind.Rejected && refused.Code == OperationErrorCode.WriteRejected, "marker.unwritable." + refused.Kind);
        Require(await context.OwnershipAsync(unwritable, token) == "ownership.new-installation", "marker.unwritable-unchanged");
    }

    private static void Expect(OperationResult result, OperationOutcomeKind kind, string code) =>
        Require(result.Kind == kind, code + "." + result.Kind + "." + result.Code);

    private static void Require(bool satisfied, string code)
    {
        if (!satisfied)
            throw new SshScenarioException("ssh-runtime." + code);
    }

    private sealed class Context(OwnedDockerBench bench, SshProbeCredentials credentials, SshHostKey pin, string root)
    {
        private static readonly Guid Installation = Guid.NewGuid();

        private readonly OperationJournalScope scope = new(Installation, Guid.NewGuid(), PinnedRuntimeProfile.Fingerprint);

        public OwnedDockerBench Bench => bench;

        private InstallationOwner Owner => new(Installation, PinnedRuntimeProfile.Fingerprint);

        public OwnershipMarkerRequest NewRequest(string configurationRoot)
        {
            var resource = new ResourceIdentity(Installation, Guid.NewGuid(), "nas", PinnedRuntimeProfile.Fingerprint,
                bench.SshTarget.EndpointFingerprint);
            return new(new(Guid.NewGuid(), resource), new("/volume1/data", configurationRoot), Owner);
        }

        public async Task<OperationResult> RunAsync(OwnershipMarkerRequest request, CancellationToken token, TimeSpan? writeTimeout = null)
        {
            // One journal per configuration root; reopening it is the restart of a new process.
            var directory = Path.Combine(root, request.Identity.Resource.ResourceId.ToString("N"));
            var journal = Directory.Exists(directory)
                ? FileOperationJournal.Open(directory, scope)
                : CreateJournal(directory);
            var probe = new NasPathProbe(bench.SshTarget, credentials);
            var writer = new NasOwnershipMarkerWriter(bench.SshTarget, credentials, writeTimeout);
            var operation = new NasOwnershipMarkerOperation(request, probe, writer, pin);
            return await new ConfigurationOperationScenario(journal, operation).ExecuteAsync(operation, token);
        }

        public async Task<string> OwnershipAsync(OwnershipMarkerRequest request, CancellationToken token)
        {
            var result = await new NasPathProbe(bench.SshTarget, credentials).InspectAsync(request.Layout, pin, token);
            Require(result.Status == SshProbeStatus.Ready, "marker.inspect." + result.Status);
            return InstallationOwnershipAssessment.Evaluate(result.Snapshot!, Owner).Code;
        }

        private FileOperationJournal CreateJournal(string directory)
        {
            Directory.CreateDirectory(directory);
            return FileOperationJournal.Create(directory, scope);
        }
    }
}
