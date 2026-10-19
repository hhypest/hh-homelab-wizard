using System.Security.Cryptography;
using System.Text;
using HhHomelab.Wizard.Application;
using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using Renci.SshNet.Common;
using Xunit;

namespace HhHomelab.Wizard.UnitTests;

public sealed class QbittorrentInstallationGraphTests : IDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly string directory = Path.Combine(Path.GetTempPath(), $"wizard-graph-test-{Guid.NewGuid():N}");

    public QbittorrentInstallationGraphTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task ComposeFollowsThePinnedServiceWithTheAlignedWebUiPort()
    {
        var plan = await PlanAsync();
        var files = QbittorrentConfigurationRenderer.Render(plan);
        Assert.Equal(["media/compose.yaml", "media/.env"], files.Select(file => file.RelativePath));
        Assert.Equal([0x1A4, 0x180], files.Select(file => file.Mode));
        var compose = Text(files[0]);
        var service = PinnedRuntimeProfile.Service("qbittorrent");
        Assert.Contains("    image: " + service.Image + "\n", compose, StringComparison.Ordinal);
        Assert.Contains("      - DOCKER_MODS=" + string.Join('|', service.DockerMods) + "\n", compose, StringComparison.Ordinal);
        Assert.Contains("      - WEBUI_PORT=${QBITTORRENT_WEBUI_PORT:?см. .env}\n", compose, StringComparison.Ordinal);
        Assert.Contains("      - \"${BIND_ADDR:?см. .env}:${QBITTORRENT_WEBUI_PORT}:${QBITTORRENT_WEBUI_PORT}\"\n", compose, StringComparison.Ordinal);
        Assert.Contains("      - \"${BIND_ADDR}:${QBITTORRENT_TORRENT_PORT}:${QBITTORRENT_TORRENT_PORT}/udp\"\n", compose, StringComparison.Ordinal);
        Assert.Contains("      - ./config/qbittorrent:/config\n      - ./watch:/watch\n      - ${MEDIA_ROOT:?см. .env}:/data\n", compose, StringComparison.Ordinal);
        Assert.Contains("    name: ${MEDIA_NETWORK:?см. .env}\n", compose, StringComparison.Ordinal);
        Assert.Contains(plan.Profile.ComposeSha256, compose, StringComparison.Ordinal);
        Assert.Equal(QbittorrentConfigurationRenderer.SourceComposeSha256, plan.Profile.ComposeSha256);
        Assert.Contains("\nname: hhw-" + plan.Owner.InstallationId.ToString("N") + "\nservices:\n", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("9080", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("192.168.", compose, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnvironmentCarriesOnlyTheChosenValuesAndRendersDeterministically()
    {
        var plan = await PlanAsync();
        var environment = Text(QbittorrentConfigurationRenderer.Render(plan)[1]);
        var values = environment.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(line => !line.StartsWith('#')).ToArray();
        Assert.Equal(
        [
            "PUID=1026", "PGID=100", "UMASK=002", "TZ=Europe/Moscow", "BIND_ADDR=0.0.0.0",
            "MEDIA_ROOT='/volume1/data'", "MEDIA_NETWORK=media-test",
            "QBITTORRENT_WEBUI_PORT=19080", "QBITTORRENT_TORRENT_PORT=16881",
        ], values);
        var again = QbittorrentConfigurationRenderer.Render(plan);
        Assert.Equal(environment, Text(again[1]));
        Assert.EndsWith("\n", environment, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestAndProfileRenderTheSameBytesAsThePlan()
    {
        var plan = await PlanAsync();
        var fromPlan = QbittorrentConfigurationRenderer.Render(plan);
        var direct = QbittorrentConfigurationRenderer.Render(plan.Request, plan.Profile);
        Assert.Equal(fromPlan.Select(file => (file.RelativePath, file.Mode, file.Content.ToArray())),
            direct.Select(file => (file.RelativePath, file.Mode, file.Content.ToArray())));
    }

    [Theory]
    [InlineData("/volume1/da:ta")]
    [InlineData("/volume1/da'ta")]
    public async Task DataRootThatComposeCannotCarryLiterallyIsRefused(string dataRoot)
    {
        var fixture = new QbittorrentPlanTests.PlanData { Layout = new MediaPathLayout(dataRoot, PathData.Layout.ConfigurationRoot) };
        var plan = (await fixture.Compiler.CompileAsync(fixture.Request(), PreflightData.Pin, Token)).Value!;
        Assert.Contains(plan.Checks, check => check.Code == "plan.data-root.unsupported-characters" && check.Status == PreflightCheckStatus.Blocking);
        Assert.Equal("render.data-root.unsupported", Assert.Throws<ArgumentException>(() => QbittorrentConfigurationRenderer.Render(plan)).Message);
        var nas = new FakeNas();
        Assert.Equal("graph.plan.blocking", Assert.Throws<ArgumentException>(() => Graph(plan, nas)).Message);
    }

    [Fact]
    public async Task GraphPutsTheMarkerBeforeMountDirectoriesAndFilesAndIsStableAcrossProcesses()
    {
        var plan = await PlanAsync();
        var nas = new FakeNas();
        using var first = Graph(plan, nas);
        using var second = Graph(plan, nas);
        Assert.Equal(first.Plan.Fingerprint, second.Plan.Fingerprint);
        Assert.Equal(5, first.Plan.Steps.Count);
        var marker = first.Plan.ExecutionOrder[0];
        Assert.Equal(ConfigurationOperationKind.OwnershipMarker, marker.Descriptor.Kind);
        Assert.All(first.Plan.Steps.Skip(1), step => Assert.Equal([marker.Descriptor.Identity.OperationId], step.Dependencies));
        Assert.Equal(
        [
            ConfigurationOperationKind.ConfigurationDirectory, ConfigurationOperationKind.ConfigurationDirectory,
            ConfigurationOperationKind.ConfigurationFile, ConfigurationOperationKind.ConfigurationFile,
        ], first.Plan.Steps.Skip(1).Select(step => step.Descriptor.Kind));
        Assert.Equal(5, first.Plan.Steps.Select(step => step.Descriptor.Identity.Resource.ResourceId).Distinct().Count());

        var otherPort = await PlanAsync(webPort: 19081);
        using var changed = Graph(otherPort, nas);
        Assert.NotEqual(first.Plan.Fingerprint, changed.Plan.Fingerprint);
    }

    [Fact]
    public async Task OnlyAFreshPlanWithoutBlockingChecksBecomesAGraph()
    {
        var blocking = await PlanAsync(bind: "127.0.0.1");
        Assert.True(blocking.HasBlockingChecks);
        var nas = new FakeNas();
        Assert.Equal("graph.plan.blocking", Assert.Throws<ArgumentException>(() => Graph(blocking, nas)).Message);
        var plan = await PlanAsync();
        Assert.True(plan.HasUnresolvedChecks);
        Assert.Equal("graph.plan.stale", Assert.Throws<ArgumentException>(() => Graph(plan, nas, plan.ObservedAt + TimeSpan.FromMinutes(3))).Message);
        // Same endpoint, another approved key: not the key the plan's evidence was collected with.
        Assert.Equal("graph.pin.mismatch", Assert.Throws<ArgumentException>(() =>
            QbittorrentInstallationGraph.Create(plan, PreflightData.OtherPin, plan.ObservedAt,
                nas.PathProbe(), nas.MarkerWriter(), nas.DirectoryClient(), nas.FileClient())).Message);
    }

    [Theory]
    [InlineData(2042, 100)]
    [InlineData(1026, 200)]
    public async Task IncompatibleContainerIdsStopBeforeAnyGraphMutation(int userId, int groupId)
    {
        var fixture = new QbittorrentPlanTests.PlanData();
        var request = fixture.Request(userId: userId, groupId: groupId);
        var plan = (await fixture.Compiler.CompileAsync(request, PreflightData.Pin, Token)).Value!;
        var nas = new FakeNas();

        var error = Assert.Throws<ArgumentException>(() => Graph(plan, nas));

        Assert.Equal("graph.plan.blocking", error.Message);
        Assert.Equal(0, nas.MarkerWrites);
        Assert.Equal(0, nas.DirectoryWrites);
        Assert.Equal(0, nas.FileWrites);
    }

    [Fact]
    public async Task FreshnessIsMeasuredFromTheOldestEvidenceNotFromPlanConstruction()
    {
        var plan = await PlanAsync();
        var nas = new FakeNas();
        var limit = plan.EvidenceStartedAt + QbittorrentInstallationPlan.MaximumEvidenceAge;
        using var atLimit = Graph(plan, nas, limit);
        Assert.Equal("graph.plan.stale", Assert.Throws<ArgumentException>(() => Graph(plan, nas, limit + TimeSpan.FromSeconds(1))).Message);
    }

    [Fact]
    public async Task PlanRecompiledByANewProcessAfterInstallationYieldsTheSameGraph()
    {
        var before = await PlanAsync();
        var nas = new FakeNas();
        using var first = Graph(before, nas);
        var after = new QbittorrentPlanTests.PlanData
        {
            PathOutput = PathData.Output(configuration: "entry=directory:0:same:42:1026:100:755:not-empty"),
            Ownership = new(OwnershipMarkerState.Present, new(1, before.Owner.InstallationId, before.Owner.ProfileFingerprint)),
        };
        var recompiled = (await after.Compiler.CompileAsync(after.Request(), PreflightData.Pin, Token)).Value!;
        Assert.Empty(recompiled.Checks.Where(check => check.Status == PreflightCheckStatus.Blocking).Select(check => check.Code));
        Assert.NotEqual(before.Fingerprint, recompiled.Fingerprint);
        using var second = Graph(recompiled, nas);
        Assert.Equal(first.Plan.Fingerprint, second.Plan.Fingerprint);
    }

    [Fact]
    public async Task DisposedGraphCannotBeExecuted()
    {
        var plan = await PlanAsync();
        var graph = Graph(plan, new FakeNas());
        graph.Dispose();
        Assert.Throws<ObjectDisposedException>(() => graph.Operations);
        Assert.Throws<ObjectDisposedException>(() => graph.Plan);
        Assert.Throws<ObjectDisposedException>(() => graph.Ownership);
    }

    [Fact]
    public async Task GraphCreatesMarkerMountDirectoriesAndBothFilesOnceAndAResumedRunOnlyObserves()
    {
        var plan = await PlanAsync();
        var nas = new FakeNas();
        var first = await RunAsync(plan, nas);
        Assert.Equal(OperationOutcomeKind.Applied, first.Result.Kind);
        Assert.Equal(5, first.Steps.Count);
        Assert.Equal(1, nas.MarkerWrites);
        Assert.Equal(2, nas.DirectoryWrites);
        Assert.Equal(2, nas.FileWrites);
        Assert.Equal(QbittorrentInstallationGraph.MountDirectoryMode, nas.Directories["media/config/qbittorrent"]);
        Assert.Equal(QbittorrentInstallationGraph.MountDirectoryMode, nas.Directories["media/watch"]);
        var rendered = QbittorrentConfigurationRenderer.Render(plan);
        Assert.Equal(rendered[0].Content.ToArray(), nas.Files["media/compose.yaml"].Content);
        Assert.Equal(0x180, nas.Files["media/.env"].Mode);

        var resumed = await RunAsync(plan, nas);
        Assert.Equal(OperationOutcomeKind.AlreadySatisfied, resumed.Result.Kind);
        Assert.Equal(1, nas.MarkerWrites);
        Assert.Equal(2, nas.DirectoryWrites);
        Assert.Equal(2, nas.FileWrites);
    }

    [Fact]
    public async Task MountDirectoryCreatedByDockerAsRootIsAConflictAndIsNeverChanged()
    {
        var plan = await PlanAsync();
        var nas = new FakeNas();
        nas.Directories["media/watch"] = 0x1ED;
        nas.ForeignDirectories.Add("media/watch");
        var result = await RunAsync(plan, nas);
        Assert.Equal(OperationOutcomeKind.Conflict, result.Result.Kind);
        Assert.Equal(0x1ED, nas.Directories["media/watch"]);
        Assert.Contains("media/watch", nas.ForeignDirectories);
        Assert.DoesNotContain("media/watch", nas.CreatedDirectories);
    }

    [Fact]
    public async Task LostResponseOfAMountDirectoryIsUnknownThenANewProcessConfirmsWithoutRecreating()
    {
        var plan = await PlanAsync();
        var nas = new FakeNas { LoseResponseFor = "media/config/qbittorrent" };
        var lost = await RunAsync(plan, nas);
        Assert.Equal(OperationOutcomeKind.Unknown, lost.Result.Kind);
        Assert.Equal(1, nas.DirectoryWrites);
        nas.LoseResponseFor = null;
        var resumed = await RunAsync(plan, nas);
        Assert.Equal(OperationOutcomeKind.Applied, resumed.Result.Kind);
        Assert.Equal(2, nas.DirectoryWrites);
        Assert.Equal(QbittorrentInstallationGraph.MountDirectoryMode, nas.Directories["media/config/qbittorrent"]);
    }

    [Fact]
    public async Task LostResponseOfASecondFileIsUnknownThenANewProcessConfirmsWithoutRewriting()
    {
        var plan = await PlanAsync();
        var nas = new FakeNas { LoseResponseFor = "media/.env" };
        var lost = await RunAsync(plan, nas);
        Assert.Equal(OperationOutcomeKind.Unknown, lost.Result.Kind);
        Assert.Equal(5, lost.Steps.Count);
        nas.LoseResponseFor = null;
        var resumed = await RunAsync(plan, nas);
        Assert.Equal(OperationOutcomeKind.Applied, resumed.Result.Kind);
        Assert.Equal(2, nas.FileWrites);
    }

    [Fact]
    public async Task ManualEditOfTheComposeFileStopsTheResumedGraphWithoutOverwriting()
    {
        var plan = await PlanAsync();
        var nas = new FakeNas();
        Assert.Equal(OperationOutcomeKind.Applied, (await RunAsync(plan, nas)).Result.Kind);
        var edited = Encoding.UTF8.GetBytes("services: {}\n");
        nas.Files["media/compose.yaml"] = (edited, 0x1A4);
        var resumed = await RunAsync(plan, nas);
        Assert.Equal(OperationOutcomeKind.Conflict, resumed.Result.Kind);
        Assert.Equal(edited, nas.Files["media/compose.yaml"].Content);
        Assert.Equal(2, nas.FileWrites);
    }

    [Fact]
    public async Task RootOfAnotherInstallationStopsAtTheMarkerBeforeAnyFile()
    {
        var plan = await PlanAsync();
        var nas = new FakeNas { MarkerJson = OwnershipMarkerJson.Serialize(new(1, Guid.NewGuid(), PinnedRuntimeProfile.Fingerprint)) };
        var result = await RunAsync(plan, nas);
        Assert.Equal(OperationOutcomeKind.Conflict, result.Result.Kind);
        Assert.Single(result.Steps);
        Assert.Equal(0, nas.DirectoryWrites);
        Assert.Equal(0, nas.FileWrites);
    }

    [Fact]
    public async Task RouterSendsEachCheckToTheOwnerOfThatResourceOnly()
    {
        Assert.Throws<ArgumentException>(() => new OperationOwnershipRouter([new NotOwning()]));
        var owned = new Owning(ResourceOwnership.Owned);
        var unproven = new Owning(ResourceOwnership.Unproven);
        var router = new OperationOwnershipRouter([owned, unproven]);
        Assert.Equal(ResourceOwnership.Owned, await router.CheckAsync(owned.Descriptor.Identity.Resource, Token));
        Assert.Equal(ResourceOwnership.Unproven, await router.CheckAsync(unproven.Descriptor.Identity.Resource, Token));
        Assert.Equal(ResourceOwnership.Foreign, await router.CheckAsync(new NotOwning().Descriptor.Identity.Resource, Token));
        Assert.Equal(1, owned.Checks);
        Assert.Throws<ArgumentException>(() => new OperationOwnershipRouter([owned, owned]));
    }

    [Fact]
    public void StableIdsAreVersion8AndDependOnInstallationAndRole()
    {
        var installation = Guid.Parse("b3d64c85-e391-4960-b58a-c157d74acb62");
        var id = QbittorrentInstallationGraph.StableId(installation, "operation:marker");
        Assert.Equal(id, QbittorrentInstallationGraph.StableId(installation, "operation:marker"));
        Assert.NotEqual(id, QbittorrentInstallationGraph.StableId(installation, "resource:marker"));
        Assert.NotEqual(id, QbittorrentInstallationGraph.StableId(Guid.NewGuid(), "operation:marker"));
        Assert.Equal(8, id.Version);
    }

    private static async Task<QbittorrentInstallationPlan> PlanAsync(string bind = "0.0.0.0", int webPort = 19080)
    {
        var fixture = new QbittorrentPlanTests.PlanData();
        return (await fixture.Compiler.CompileAsync(fixture.Request(bind: bind, webPort: webPort), PreflightData.Pin, Token)).Value!;
    }

    private static QbittorrentInstallationGraph Graph(QbittorrentInstallationPlan plan, FakeNas nas, DateTimeOffset? now = null) =>
        QbittorrentInstallationGraph.Create(plan, PreflightData.Pin, now ?? plan.ObservedAt, nas.PathProbe(), nas.MarkerWriter(), nas.DirectoryClient(), nas.FileClient());

    /// <summary>Every run reopens the same journal and rebuilds the graph, as a new process would.</summary>
    private async Task<ConfigurationPlanResult> RunAsync(QbittorrentInstallationPlan plan, FakeNas nas)
    {
        var scope = new OperationJournalScope(plan.Owner.InstallationId, Guid.Parse("4d1c3e8b-37a3-4a0f-9f58-1a7f2fb0c6b1"), plan.Owner.ProfileFingerprint);
        var journal = File.Exists(Path.Combine(directory, "journal.json")) || Directory.EnumerateFileSystemEntries(directory).Any()
            ? FileOperationJournal.Open(directory, scope)
            : FileOperationJournal.Create(directory, scope);
        using var graph = Graph(plan, nas);
        return await new ConfigurationPlanScenario(journal, graph.Ownership).ExecuteAsync(graph.Plan, graph.Operations, Token);
    }

    private static string Text(RenderedConfigurationFile file) => Encoding.UTF8.GetString(file.Content);

    public void Dispose()
    {
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (Path.GetDirectoryName(Path.GetFullPath(directory)) != parent)
            throw new InvalidOperationException("test.directory.invalid");
        Directory.Delete(directory, recursive: true);
    }

    private sealed class NotOwning : IConfigurationOperation
    {
        public OperationDescriptor Descriptor { get; } = new(new(Guid.NewGuid(), new(Guid.NewGuid(), Guid.NewGuid(), "nas",
            new string('a', 64), new string('b', 64))), ConfigurationOperationKind.ConfigurationFile, new string('c', 64));

        public Task<OperationRead<OperationObservation>> ObserveAsync(CancellationToken token) => throw new NotSupportedException();

        public Task<OperationWriteAttempt> ApplyAsync(OperationObservation baseline, CancellationToken token) => throw new NotSupportedException();

        public Task<OperationRead<OperationVerification>> VerifyAsync(CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class Owning(ResourceOwnership answer) : IConfigurationOperation, IOperationOwnership
    {
        public int Checks { get; private set; }

        public OperationDescriptor Descriptor { get; } = new NotOwning().Descriptor;

        public Task<ResourceOwnership> CheckAsync(ResourceIdentity resource, CancellationToken token)
        {
            Checks++;
            return Task.FromResult(answer);
        }

        public Task<OperationRead<OperationObservation>> ObserveAsync(CancellationToken token) => throw new NotSupportedException();

        public Task<OperationWriteAttempt> ApplyAsync(OperationObservation baseline, CancellationToken token) => throw new NotSupportedException();

        public Task<OperationRead<OperationVerification>> VerifyAsync(CancellationToken token) => throw new NotSupportedException();
    }

    /// <summary>One synthetic NAS for paths, marker, directories and files, following the fixed scripts' rules:
    /// the marker is published once; directories and files only below the exact marker; an existing directory
    /// or file is never changed.</summary>
    private sealed class FakeNas
    {
        public string? MarkerJson { get; set; }

        public Dictionary<string, (byte[] Content, int Mode)> Files { get; } = new(StringComparer.Ordinal);

        /// <summary>Directory path to its permission bits.</summary>
        public Dictionary<string, int> Directories { get; } = new(StringComparer.Ordinal);

        /// <summary>Directories not owned by the SSH user, for example created by Docker as root.</summary>
        public HashSet<string> ForeignDirectories { get; } = new(StringComparer.Ordinal);

        public HashSet<string> CreatedDirectories { get; } = new(StringComparer.Ordinal);

        public string? LoseResponseFor { get; set; }

        public int MarkerWrites { get; private set; }

        public int DirectoryWrites { get; private set; }

        public int FileWrites { get; private set; }

        public NasPathProbe PathProbe() => new(PreflightData.Target, _ => new Wire(this), TimeSpan.FromSeconds(5), TimeProvider.System);

        public NasOwnershipMarkerWriter MarkerWriter() => new(PreflightData.Target, _ => new Wire(this), TimeSpan.FromSeconds(5));

        public NasConfigurationDirectoryClient DirectoryClient() => new(PreflightData.Target, _ => new Wire(this), TimeSpan.FromSeconds(5));

        public NasConfigurationFileClient FileClient() => new(PreflightData.Target, _ => new Wire(this), TimeSpan.FromSeconds(5));

        private sealed class Wire(FakeNas nas) : INasPathWire, INasDirectoryWire
        {
            public Task ConnectAsync(Func<string, byte[], bool> acceptKey, CancellationToken token)
            {
                if (!acceptKey("ssh-ed25519", PreflightData.PublicKey))
                    throw new SshConnectionException("synthetic-host-key-refused");
                return Task.CompletedTask;
            }

            public Task<RemotePathFacts> ReadPathsAsync(MediaPathLayout layout, CancellationToken token)
            {
                var root = nas.MarkerJson is null ? null : "entry=directory:0:same:42:1026:100:755:not-empty";
                var marker = nas.MarkerJson is { } json
                    ? "marker=present\nmarker-content=" + Convert.ToHexStringLower(Encoding.UTF8.GetBytes(json))
                    : "marker=absent";
                return Task.FromResult(NasPathProtocol.Parse(layout, 0, PathData.Output(configuration: root, marker: marker)));
            }

            public Task<MarkerWriteOutcome> WriteOwnershipMarkerAsync(OwnershipMarkerRequest request, Action sending, CancellationToken token)
            {
                sending();
                if (nas.MarkerJson is not null)
                    return Task.FromResult(MarkerWriteOutcome.Rejected);
                nas.MarkerWrites++;
                nas.MarkerJson = OwnershipMarkerJson.Serialize(request.Marker);
                return Task.FromResult(MarkerWriteOutcome.Created);
            }

            public Task<(int? ExitCode, string Output)> ReadConfigurationFileAsync(string request, CancellationToken token)
            {
                var lines = request.Split('\n');
                var marker = Match(lines[2]["marker=".Length..]);
                var file = "not-checked";
                if (marker == "same")
                {
                    file = nas.Files.TryGetValue(lines[3]["path=".Length..], out var existing)
                        ? $"present:{existing.Content.Length}:{Convert.ToString(existing.Mode, 8)}:{Convert.ToHexStringLower(SHA256.HashData(existing.Content))}"
                        : "absent";
                }
                return Task.FromResult<(int?, string)>((0, $"hh-homelab-wizard:nas-file:v1\nmarker={marker}\nfile={file}\nend\n"));
            }

            public Task<(int? ExitCode, string Output)> WriteConfigurationFileAsync(byte[] request, Action sending, CancellationToken token)
            {
                sending();
                var end = IndexOf(request, "\nend\n"u8) + 5;
                var lines = Encoding.UTF8.GetString(request, 0, end).Split('\n');
                var path = lines[3]["path=".Length..];
                if (Match(lines[2]["marker=".Length..]) != "same" || nas.Files.ContainsKey(path))
                    return Task.FromResult<(int?, string)>((3, ""));
                nas.FileWrites++;
                nas.Files[path] = (request[end..], Convert.ToInt32(lines[4]["mode=".Length..], 8));
                if (nas.LoseResponseFor == path)
                    throw new SshConnectionException("synthetic-lost-response");
                return Task.FromResult<(int?, string)>((0, "file=created\n"));
            }

            public Task<(int? ExitCode, string Output)> ReadConfigurationDirectoryAsync(string request, CancellationToken token)
            {
                var lines = request.Split('\n');
                var marker = Match(lines[2]["marker=".Length..]);
                var directory = "not-checked";
                if (marker == "same")
                {
                    var path = lines[3]["path=".Length..];
                    directory = nas.Directories.TryGetValue(path, out var mode)
                        ? $"present:{Convert.ToString(mode, 8)}:{(nas.ForeignDirectories.Contains(path) ? "other" : "own")}"
                        : "absent";
                }
                return Task.FromResult<(int?, string)>((0, $"hh-homelab-wizard:nas-directory:v1\nmarker={marker}\ndirectory={directory}\nend\n"));
            }

            public Task<(int? ExitCode, string Output)> CreateConfigurationDirectoryAsync(string request, Action sending, CancellationToken token)
            {
                sending();
                var lines = request.Split('\n');
                var path = lines[3]["path=".Length..];
                var mode = Convert.ToInt32(lines[4]["mode=".Length..], 8);
                if (Match(lines[2]["marker=".Length..]) != "same")
                    return Task.FromResult<(int?, string)>((3, ""));
                if (nas.Directories.TryGetValue(path, out var existing))
                {
                    var same = existing == mode && !nas.ForeignDirectories.Contains(path);
                    return Task.FromResult<(int?, string)>(same ? (0, "directory=already-present\n") : (3, ""));
                }
                nas.DirectoryWrites++;
                nas.Directories[path] = mode;
                nas.CreatedDirectories.Add(path);
                if (nas.LoseResponseFor == path)
                    throw new SshConnectionException("synthetic-lost-response");
                return Task.FromResult<(int?, string)>((0, "directory=created\n"));
            }

            public Task<(int? ExitCode, string Output)> RunLockAsync(string request, Action sending, CancellationToken token) =>
                throw new InvalidOperationException("test.no-lock");

            public void Dispose()
            {
            }

            private string Match(string marker) => nas.MarkerJson is null ? "absent" : marker == nas.MarkerJson ? "same" : "different";

            private static int IndexOf(byte[] data, ReadOnlySpan<byte> pattern) => data.AsSpan().IndexOf(pattern);
        }
    }
}
