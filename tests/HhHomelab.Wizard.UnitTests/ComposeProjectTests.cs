using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using Renci.SshNet.Common;
using Xunit;

namespace HhHomelab.Wizard.UnitTests;

public sealed class ComposeProjectTests
{
    private const string Project = "hhw-b3d64c85e3914960b58ac157d74acb62";

    private const string Directory = "/volume1/docker/hh-homelab/media";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string NetworkId => new('a', 64);

    private static string ContainerId => new('c', 64);

    private static ComposeProjectIdentity Identity => new(Project, "media", "media-test", "qbittorrent", "qbittorrent", Directory);

    [Fact]
    public void RenderedProjectIdentityMatchesTheComposeFileLabels()
    {
        var fixture = new QbittorrentPlanTests.PlanData();

        var identity = QbittorrentConfigurationRenderer.ProjectIdentity(fixture.Request(), fixture.Profile.Definition);

        Assert.Equal(Project, identity.ProjectName);
        Assert.Equal(QbittorrentConfigurationRenderer.NetworkKey, identity.NetworkKey);
        Assert.Equal("media-test", identity.NetworkName);
        Assert.Equal("qbittorrent", identity.ServiceName);
        Assert.Equal("qbittorrent", identity.ContainerName);
        Assert.Equal(Directory, identity.WorkingDirectory);
    }

    [Theory]
    [InlineData("hhw-B3D64C85E3914960B58AC157D74ACB62")]
    [InlineData("hhw-b3d64c85e3914960b58ac157d74acb6")]
    [InlineData("media")]
    [InlineData("hhw-b3d64c85e3914960b58ac157d74acb6g")]
    public void ProjectNameMustBeTheRenderedInstallationName(string name)
    {
        Assert.False(ComposeProjectIdentity.IsProjectName(name));
        Assert.Equal("compose.identity.project", Assert.Throws<ArgumentException>(() =>
            new ComposeProjectIdentity(name, "media", "media-test", "qbittorrent", "qbittorrent", Directory)).Message);
    }

    [Theory]
    [InlineData("qbit\"torrent")]
    [InlineData("-qbittorrent")]
    [InlineData("qbit torrent")]
    [InlineData("{{.ID}}")]
    [InlineData("")]
    public void NamesCompareSafelyInsideGoTemplates(string name)
    {
        Assert.False(ComposeProjectIdentity.IsDockerName(name));
        Assert.Throws<ArgumentException>(() => new ComposeProjectIdentity(Project, "media", "media-test", name, "qbittorrent", Directory));
        Assert.Throws<ArgumentException>(() => new ComposeProjectIdentity(Project, "media", "media-test", "qbittorrent", name, Directory));
    }

    [Theory]
    [InlineData("relative/media")]
    [InlineData("/tmp/media")]
    [InlineData("/volume1/docker/../media")]
    public void WorkingDirectoryMustBeAVolumePath(string directory)
    {
        Assert.Equal("compose.identity.directory", Assert.Throws<ArgumentException>(() =>
            new ComposeProjectIdentity(Project, "media", "media-test", "qbittorrent", "qbittorrent", directory)).Message);
    }

    [Fact]
    public void NoMatchesIsAbsentOnlyWhenDockerWasRead()
    {
        Assert.Equal(ComposeProjectState.Absent, Facts().State);
        Assert.Equal(ComposeProjectState.Unavailable, new ComposeProjectFacts(PreflightFactStatus.Unavailable, [], []).State);
    }

    [Fact]
    public void OwnNetworkWithOrWithoutOwnContainerIsOwned()
    {
        var networkOnly = Facts([Network()]);
        var running = Facts([Network()], [Container(state: ComposeContainerState.Running)]);

        Assert.Equal(ComposeProjectState.Owned, networkOnly.State);
        Assert.Null(networkOnly.OwnContainer);
        Assert.Equal(ComposeProjectState.Owned, running.State);
        Assert.Equal(ComposeContainerState.Running, running.OwnContainer!.State);
    }

    [Fact]
    public void AnyForeignMatchMakesTheProjectForeignEvenNextToOwnResources()
    {
        var foreignNetwork = Facts([Network(ownership: ComposeResourceOwnership.Foreign)]);
        var foreignContainer = Facts([Network()], [Container(ownership: ComposeResourceOwnership.Foreign)]);

        Assert.Equal(ComposeProjectState.Foreign, foreignNetwork.State);
        Assert.Equal(ComposeProjectState.Foreign, foreignContainer.State);
        Assert.Null(foreignContainer.OwnContainer);
    }

    [Fact]
    public void InconsistentOwnResourcesAreAmbiguous()
    {
        var containerWithoutNetwork = Facts([], [Container()]);
        var twoContainers = Facts([Network()], [Container(), Container(id: new string('d', 64))]);

        Assert.Equal(ComposeProjectState.Ambiguous, containerWithoutNetwork.State);
        Assert.Equal(ComposeProjectState.Ambiguous, twoContainers.State);
    }

    [Fact]
    public void FactsRejectPartialDuplicateTruncatedAndExcessiveMatches()
    {
        Assert.Throws<ArgumentException>(() => new ComposeProjectFacts(PreflightFactStatus.Unavailable, [Network()], []));
        Assert.Throws<ArgumentException>(() => Facts([Network(), Network(id: NetworkId.ToUpperInvariant())]));
        Assert.Throws<ArgumentException>(() => Facts([], [Container(id: ContainerId[..12])]));
        Assert.Throws<ArgumentException>(() => Facts(Enumerable.Range(0, ComposeProjectFacts.MaximumMatches + 1)
            .Select(number => Network(id: number.ToString("x64"))).ToArray()));
        Assert.Throws<ArgumentException>(() => Facts([], [Container(state: (ComposeContainerState)99)]));
    }

    [Fact]
    public void SwarmNetworkIdIsReadButNeverChangesOwnershipRules()
    {
        var facts = Facts([Network(id: "abcdefghijklmnopqrstuvwxy", ownership: ComposeResourceOwnership.Foreign)]);

        Assert.Equal(ComposeProjectState.Foreign, facts.State);
    }

    [Fact]
    public void SnapshotFingerprintIgnoresTimeAndOrderButNotOwnershipOrState()
    {
        var created = Snapshot(Facts([Network()], [Container()]));
        var later = Snapshot(Facts([Network()], [Container()]), PreflightData.Now.AddSeconds(30));
        var running = Snapshot(Facts([Network()], [Container(state: ComposeContainerState.Running)]));
        var foreign = Snapshot(Facts([Network()], [Container(ownership: ComposeResourceOwnership.Foreign)]));

        Assert.Equal(created.EnvironmentFingerprint, later.EnvironmentFingerprint);
        Assert.NotEqual(created.EnvironmentFingerprint, running.EnvironmentFingerprint);
        Assert.NotEqual(created.EnvironmentFingerprint, foreign.EnvironmentFingerprint);
        Assert.Equal("compose.snapshot", created.ToString());
    }

    [Fact]
    public void SnapshotRequiresKeyOfTheSameEndpoint()
    {
        var otherTarget = new SshConnectionTarget("other.example.invalid", 22, "synthetic-user");

        Assert.Throws<ArgumentException>(() => new ComposeProjectSnapshot(otherTarget, Identity, PreflightData.Pin,
            PreflightData.Profile, PreflightData.Now, Facts()));
    }

    [Fact]
    public void RequestCarriesEveryValueOnItsOwnLine()
    {
        Assert.Equal("hh-homelab-wizard:nas-compose-request:v1\nproject=" + Project + "\nnetwork-key=media\nnetwork=media-test\n"
            + "service=qbittorrent\ncontainer=qbittorrent\ndirectory=" + Directory + "\nend\n", NasComposeProjectProtocol.Request(Identity));
    }

    [Fact]
    public void ParserReadsNetworksThenContainers()
    {
        var facts = NasComposeProjectProtocol.Parse(0, Output(["network=" + NetworkId + ":own"],
            ["container=" + ContainerId + ":own:exited"]));

        Assert.Equal(ComposeProjectState.Owned, facts.State);
        Assert.Equal(ComposeContainerState.Exited, facts.OwnContainer!.State);
        Assert.Equal(ComposeProjectState.Absent, NasComposeProjectProtocol.Parse(0, Output()).State);
        Assert.Equal(ComposeProjectState.Unavailable, NasComposeProjectProtocol.Parse(0, Output(status: "unavailable")).State);
    }

    [Theory]
    [InlineData(1, "")]
    [InlineData(0, "wrong-marker")]
    [InlineData(0, "missing-end")]
    [InlineData(0, "unknown-record")]
    [InlineData(0, "network-after-container")]
    [InlineData(0, "unknown-state")]
    [InlineData(0, "unknown-ownership")]
    [InlineData(0, "extra-field")]
    [InlineData(0, "records-with-unavailable")]
    [InlineData(0, "short-id")]
    [InlineData(0, "trailing-text")]
    public void ParserRejectsAnyDeviation(int exitCode, string variant)
    {
        var network = "network=" + NetworkId + ":own";
        var container = "container=" + ContainerId + ":own:running";
        var output = variant switch
        {
            "wrong-marker" => Output().Replace("nas-compose:v1", "nas-compose:v2", StringComparison.Ordinal),
            "missing-end" => Output()[..^4],
            "unknown-record" => Output(["volume=" + NetworkId]),
            "network-after-container" => Output([container, network]),
            "unknown-state" => Output([network, "container=" + ContainerId + ":own:healthy"]),
            "unknown-ownership" => Output(["network=" + NetworkId + ":mine"]),
            "extra-field" => Output(["network=" + NetworkId + ":own:extra"]),
            "records-with-unavailable" => Output([network], status: "unavailable"),
            "short-id" => Output(["container=" + ContainerId[..12] + ":own:running"]),
            "trailing-text" => Output() + "more\n",
            _ => Output(),
        };

        Assert.Throws<InvalidDataException>(() => NasComposeProjectProtocol.Parse(exitCode, output));
    }

    [Fact]
    public async Task ProbeReturnsSnapshotBoundToTheApprovedKey()
    {
        var wire = new ComposeWire { Facts = Facts([Network()]) };

        var result = await Probe(wire).InspectAsync(Identity, PreflightData.Pin, Token);

        Assert.Equal(SshProbeStatus.Ready, result.Status);
        Assert.Equal(PreflightData.Pin, result.Snapshot!.HostKey);
        Assert.Equal(ComposeProjectState.Owned, result.Snapshot.Facts.State);
        Assert.Equal(PreflightData.Now, result.Snapshot.ObservedAt);
        Assert.Equal(Identity.ProjectName, wire.Identity!.ProjectName);
        Assert.True(wire.Disposed);
    }

    [Fact]
    public async Task ProbeRefusesAKeyOfAnotherEndpointWithoutConnecting()
    {
        var wire = new ComposeWire();
        var otherEndpoint = SshHostKey.FromPublicKey(new string('f', 64), "ssh-ed25519", PreflightData.PublicKey);

        var result = await Probe(wire).InspectAsync(Identity, otherEndpoint, Token);

        Assert.Equal(SshProbeStatus.InvalidInput, result.Status);
        Assert.Equal(0, wire.Connects);
    }

    [Fact]
    public async Task ChangedHostKeyIsNeverAnObservation()
    {
        var wire = new ComposeWire();

        var result = await Probe(wire).InspectAsync(Identity, PreflightData.OtherPin, Token);

        Assert.Null(result.Snapshot);
        Assert.NotEqual(SshProbeStatus.Ready, result.Status);
        Assert.Equal(0, wire.Reads);
    }

    [Fact]
    public async Task MalformedAnswerIsInvalidResponse()
    {
        var wire = new ComposeWire { Read = _ => throw new InvalidDataException("compose.response.invalid") };

        var result = await Probe(wire).InspectAsync(Identity, PreflightData.Pin, Token);

        Assert.Equal(SshProbeStatus.InvalidResponse, result.Status);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public async Task DeadlineAndCallerCancellationAreDistinguished()
    {
        var hanging = new ComposeWire { Read = async token => { await Task.Delay(Timeout.Infinite, token); return Facts(); } };
        var timedOut = await Probe(hanging, TimeSpan.FromMilliseconds(50)).InspectAsync(Identity, PreflightData.Pin, Token);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancelled.Cancel();

        var byCaller = await Probe(new ComposeWire()).InspectAsync(Identity, PreflightData.Pin, cancelled.Token);

        Assert.Equal(SshProbeStatus.TimedOut, timedOut.Status);
        Assert.Equal(SshProbeStatus.Cancelled, byCaller.Status);
    }

    [Fact]
    public async Task ConcurrentInspectionIsBusyInsteadOfSharingAConnection()
    {
        var gate = new TaskCompletionSource();
        var wire = new ComposeWire { Read = async _ => { await gate.Task; return Facts(); } };
        var probe = Probe(wire);

        var first = probe.InspectAsync(Identity, PreflightData.Pin, Token);
        while (wire.Reads == 0)
            await Task.Delay(5, Token);
        var second = await probe.InspectAsync(Identity, PreflightData.Pin, Token);
        gate.SetResult();

        Assert.Equal(SshProbeStatus.Busy, second.Status);
        Assert.Equal(SshProbeStatus.Ready, (await first).Status);
    }

    private static ComposeNetworkMatch Network(string? id = null, ComposeResourceOwnership ownership = ComposeResourceOwnership.Own) =>
        new(id ?? NetworkId, ownership);

    private static ComposeContainerMatch Container(string? id = null, ComposeResourceOwnership ownership = ComposeResourceOwnership.Own,
        ComposeContainerState state = ComposeContainerState.Created) => new(id ?? ContainerId, ownership, state);

    private static ComposeProjectFacts Facts(ComposeNetworkMatch[]? networks = null, ComposeContainerMatch[]? containers = null) =>
        new(PreflightFactStatus.Available, networks ?? [], containers ?? []);

    private static ComposeProjectSnapshot Snapshot(ComposeProjectFacts facts, DateTimeOffset? observed = null) =>
        new(PreflightData.Target, Identity, PreflightData.Pin, PreflightData.Profile, observed ?? PreflightData.Now, facts);

    private static string Output(string[]? records = null, string[]? containers = null, string status = "available") =>
        NasComposeProjectProtocol.ResponseMarker + "\n" + string.Concat((records ?? []).Concat(containers ?? []).Select(line => line + "\n"))
        + "status=" + status + "\nend\n";

    private static NasComposeProjectProbe Probe(ComposeWire wire, TimeSpan? timeout = null) =>
        new(PreflightData.Target, _ => wire, timeout ?? TimeSpan.FromSeconds(5), new Clock());

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => PreflightData.Now;
    }

    private sealed class ComposeWire : INasComposeWire
    {
        internal bool Disposed { get; private set; }

        internal int Connects { get; private set; }

        internal int Reads { get; private set; }

        internal ComposeProjectIdentity? Identity { get; private set; }

        internal ComposeProjectFacts Facts = new(PreflightFactStatus.Available, [], []);

        internal Func<CancellationToken, Task<ComposeProjectFacts>>? Read;

        public Task ConnectAsync(Func<string, byte[], bool> acceptKey, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Connects++;
            if (!acceptKey("ssh-ed25519", PreflightData.PublicKey))
                throw new SshConnectionException("synthetic-key-rejected");

            return Task.CompletedTask;
        }

        public Task<ComposeProjectFacts> ReadComposeProjectAsync(ComposeProjectIdentity identity, CancellationToken token)
        {
            Reads++;
            Identity = identity;
            return Read?.Invoke(token) ?? Task.FromResult(Facts);
        }

        public void Dispose() => Disposed = true;
    }
}
