using System.Text;
using HhHomelab.Wizard.Application;
using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using HhHomelab.Wizard.ProbeSupport;

/// <summary>Configuration files below the own marked root /volume1/apps/created, written by the unprivileged
/// SSH user through the shared operation scenario and a real local journal.</summary>
internal static partial class MarkerScenarios
{
    private static readonly byte[] Compose = Encoding.UTF8.GetBytes("services:\n  qbittorrent:\n    image: synthetic-fixture\n");

    private static readonly byte[] Environment = Encoding.UTF8.GetBytes("WEBUI_PORT=8080\n");

    private static async Task VerifyConfigurationFilesAsync(Context context, CancellationToken token)
    {
        await context.Bench.PrepareSshFileTreeAsync(token);
        var compose = context.NewFileRequest("/volume1/apps/created", "media/compose.yaml", 0x1A4, Compose);
        Expect(await context.RunFileAsync(compose, token), OperationOutcomeKind.Applied, "file.created");
        Expect(await context.RunFileAsync(compose, token), OperationOutcomeKind.AlreadySatisfied, "file.resume-observes");

        // The script really creates the owner-only file but the response is withheld.
        var environment = context.NewFileRequest("/volume1/apps/created", "media/.env", 0x180, Environment);
        await context.Bench.SetSshFaultAsync(SshFixtureFault.FileLostResponse, token);
        var lost = await context.RunFileAsync(environment, token, TimeSpan.FromSeconds(3));
        await context.Bench.SetSshFaultAsync(SshFixtureFault.Normal, token);
        Require(lost.Kind == OperationOutcomeKind.Unknown && lost.Code == OperationErrorCode.WriteUncertain, "file.lost." + lost.Kind);
        Expect(await context.RunFileAsync(environment, token), OperationOutcomeKind.Applied, "file.lost-resumed");
        Expect(await context.RunFileAsync(environment, token), OperationOutcomeKind.AlreadySatisfied, "file.lost-settled");

        var nested = context.NewFileRequest("/volume1/apps/created", "media/config/qbittorrent/placeholder.conf", 0x180, []);
        Expect(await context.RunFileAsync(nested, token), OperationOutcomeKind.Applied, "file.nested-parents");

        await context.Bench.AppendSshManualEditAsync("media/compose.yaml", token);
        Expect(await context.RunFileAsync(compose, token), OperationOutcomeKind.Conflict, "file.manual-edit-kept");
        var edited = await context.ReadFileAsync(compose, token);
        Require(edited is { Marker: RemoteMarkerMatch.Same, File: RemoteFileState.Present } && edited.Size == Compose.Length + 17, "file.manual-edit-unchanged");

        var foreign = context.NewFileRequest("/volume1/docker/foreign", "media/compose.yaml", 0x1A4, Compose);
        Expect(await context.RunFileAsync(foreign, token), OperationOutcomeKind.Conflict, "file.foreign-root");
        var unmarked = context.NewFileRequest("/volume1/docker/hh", "media/compose.yaml", 0x1A4, Compose);
        Expect(await context.RunFileAsync(unmarked, token), OperationOutcomeKind.Conflict, "file.unmarked-root");
        var linked = context.NewFileRequest("/volume1/apps/created", "linked/compose.yaml", 0x1A4, Compose);
        Expect(await context.RunFileAsync(linked, token), OperationOutcomeKind.Conflict, "file.symlinked-parent");
        Require(await context.Bench.SshFileVictimUntouchedAsync(token), "file.symlink-target-untouched");
    }

    private sealed partial class Context
    {
        public ConfigurationFileRequest NewFileRequest(string configurationRoot, string relativePath, int mode, byte[] content)
        {
            var resource = new ResourceIdentity(Installation, Guid.NewGuid(), "nas", PinnedRuntimeProfile.Fingerprint,
                bench.SshTarget.EndpointFingerprint);
            return new(new(Guid.NewGuid(), resource), new("/volume1/data", configurationRoot), Owner, relativePath, mode, content);
        }

        public async Task<OperationResult> RunFileAsync(ConfigurationFileRequest request, CancellationToken token, TimeSpan? timeout = null)
        {
            // One journal per file resource; reopening it is the restart of a new process.
            var directory = Path.Combine(root, request.Identity.Resource.ResourceId.ToString("N"));
            var journal = Directory.Exists(directory)
                ? FileOperationJournal.Open(directory, scope)
                : CreateJournal(directory);
            var client = new NasConfigurationFileClient(bench.SshTarget, credentials, timeout);
            var operation = new NasConfigurationFileOperation(request, client, pin);
            return await new ConfigurationOperationScenario(journal, operation).ExecuteAsync(operation, token);
        }

        public async Task<RemoteFileObservation?> ReadFileAsync(ConfigurationFileRequest request, CancellationToken token)
        {
            var read = await new NasConfigurationFileClient(bench.SshTarget, credentials).ReadAsync(request, pin, token);
            return read.Observation;
        }
    }
}
