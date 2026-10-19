using HhHomelab.Wizard.Application;
using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using HhHomelab.Wizard.ProbeSupport;

/// <summary>Configuration directories below the own marked root /volume1/apps/created, created by the
/// unprivileged SSH user through the shared operation scenario and a real local journal. Runs after the
/// file scenarios, which leave media/ and a symlinked linked/ behind.</summary>
internal static partial class MarkerScenarios
{
    private static async Task VerifyConfigurationDirectoriesAsync(Context context, CancellationToken token)
    {
        // The file scenario already created this directory as a parent (default 755): it is satisfied as is.
        var configuration = context.NewDirectoryRequest("/volume1/apps/created", "media/config/qbittorrent", 0x1ED);
        Expect(await context.RunDirectoryAsync(configuration, token), OperationOutcomeKind.AlreadySatisfied, "directory.existing-parent-satisfied");

        var watch = context.NewDirectoryRequest("/volume1/apps/created", "media/watch", 0x1ED);
        Expect(await context.RunDirectoryAsync(watch, token), OperationOutcomeKind.Applied, "directory.created");
        Expect(await context.RunDirectoryAsync(watch, token), OperationOutcomeKind.AlreadySatisfied, "directory.resume-observes");

        // The script really creates the nested directory but the response is withheld.
        var nested = context.NewDirectoryRequest("/volume1/apps/created", "media/state/deep/leaf", 0x1C0);
        await context.Bench.SetSshFaultAsync(SshFixtureFault.DirectoryLostResponse, token);
        var lost = await context.RunDirectoryAsync(nested, token, TimeSpan.FromSeconds(3));
        await context.Bench.SetSshFaultAsync(SshFixtureFault.Normal, token);
        Require(lost.Kind == OperationOutcomeKind.Unknown && lost.Code == OperationErrorCode.WriteUncertain, "directory.lost." + lost.Kind);
        Expect(await context.RunDirectoryAsync(nested, token), OperationOutcomeKind.Applied, "directory.lost-resumed");

        var otherMode = context.NewDirectoryRequest("/volume1/apps/created", "media/watch", 0x1C0);
        Expect(await context.RunDirectoryAsync(otherMode, token), OperationOutcomeKind.Conflict, "directory.other-mode-kept");
        var overFile = context.NewDirectoryRequest("/volume1/apps/created", "media/compose.yaml", 0x1ED);
        Expect(await context.RunDirectoryAsync(overFile, token), OperationOutcomeKind.Conflict, "directory.file-in-the-way");
        var linked = context.NewDirectoryRequest("/volume1/apps/created", "linked/new", 0x1ED);
        Expect(await context.RunDirectoryAsync(linked, token), OperationOutcomeKind.Conflict, "directory.symlinked-parent");
        Require(await context.Bench.SshFileVictimUntouchedAsync(token), "directory.symlink-target-untouched");
        var foreign = context.NewDirectoryRequest("/volume1/docker/foreign", "media/watch", 0x1ED);
        Expect(await context.RunDirectoryAsync(foreign, token), OperationOutcomeKind.Conflict, "directory.foreign-root");
    }

    private sealed partial class Context
    {
        public ConfigurationDirectoryRequest NewDirectoryRequest(string configurationRoot, string relativePath, int mode)
        {
            var resource = new ResourceIdentity(Installation, Guid.NewGuid(), "nas", PinnedRuntimeProfile.Fingerprint,
                bench.SshTarget.EndpointFingerprint);
            return new(new(Guid.NewGuid(), resource), new("/volume1/data", configurationRoot), Owner, relativePath, mode);
        }

        public async Task<OperationResult> RunDirectoryAsync(ConfigurationDirectoryRequest request, CancellationToken token, TimeSpan? timeout = null)
        {
            // One journal per directory resource; reopening it is the restart of a new process.
            var directory = Path.Combine(root, request.Identity.Resource.ResourceId.ToString("N"));
            var journal = Directory.Exists(directory)
                ? FileOperationJournal.Open(directory, scope)
                : CreateJournal(directory);
            var client = new NasConfigurationDirectoryClient(bench.SshTarget, credentials, timeout);
            var operation = new NasConfigurationDirectoryOperation(request, client, pin);
            return await new ConfigurationOperationScenario(journal, operation).ExecuteAsync(operation, token);
        }
    }
}
