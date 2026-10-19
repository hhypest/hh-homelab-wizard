using System.Text.Json.Nodes;
using HhHomelab.Wizard.Application;
using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using Xunit;

namespace HhHomelab.Wizard.UnitTests;

public sealed class QbittorrentPlanTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PreviewNamesConcreteResourcesAndPreservesTheMediaLibrary()
    {
        var fixture = new PlanData();
        var result = await fixture.Compiler.CompileAsync(fixture.Request(), PreflightData.Pin, Token);
        var plan = Assert.IsType<QbittorrentInstallationPlan>(result.Value);
        Assert.False(plan.HasBlockingChecks);
        Assert.True(plan.HasUnresolvedChecks);
        Assert.Equal(PinnedRuntimeProfile.Service("qbittorrent").Image, plan.Profile.Image);
        Assert.Equal(PinnedRuntimeProfile.Service("qbittorrent").DockerMods, plan.Profile.DockerMods);
        Assert.Contains(plan.Resources, item => item.Kind == PlannedResourceKind.File && item.Location == PathData.Layout.ConfigurationRoot + "/media/compose.yaml");
        Assert.Contains(plan.Resources, item => item.Kind == PlannedResourceKind.File && item.Location.EndsWith("/.hh-homelab-wizard/installation.json", StringComparison.Ordinal));
        Assert.Contains(plan.Resources, item => item.ContainerPath == "/config" && item.Location.EndsWith("/media/config/qbittorrent", StringComparison.Ordinal));
        Assert.Contains(plan.Resources, item => item.ContainerPath == "/watch" && item.Location.EndsWith("/media/watch", StringComparison.Ordinal));
        Assert.Contains(plan.Resources, item => item.Kind == PlannedResourceKind.Container && item.Location == "qbittorrent" && item.Owner == plan.Owner);
        Assert.All(plan.Resources.Where(item => item.Location.StartsWith(PathData.Layout.DataRoot, StringComparison.Ordinal)), item =>
        {
            Assert.Equal(PlannedResourceState.PreserveOrCreate, item.ExpectedState);
            Assert.Null(item.Owner);
        });
        Assert.Contains(plan.Checks, check => check.Code == "paths.dsm-acl" && check.Status == PreflightCheckStatus.NotChecked);
        Assert.Contains(plan.Checks, check => check.Code == "plan.udp-ports" && check.Status == PreflightCheckStatus.NotChecked);
        Assert.Contains(plan.Checks, check => check.Code == "plan.configuration-children" && check.Status == PreflightCheckStatus.NotChecked);
        Assert.Equal(1, fixture.Host.Calls);
        Assert.Equal(1, fixture.Paths.Calls);
    }

    [Theory]
    [InlineData("0.0.0.0", "127.0.0.1", false)]
    [InlineData("192.0.2.53", "192.0.2.53", false)]
    [InlineData("127.0.0.1", "127.0.0.1", true)]
    public async Task AddressContextsRespectBindAddressAndHostVersusContainerPorts(string bind, string haHost, bool proxyRequired)
    {
        var fixture = new PlanData();
        var plan = (await fixture.Compiler.CompileAsync(fixture.Request(bind: bind, webPort: 19080), PreflightData.Pin, Token)).Value!;
        var computer = plan.Endpoints.Single(endpoint => endpoint.Context == PlannedAddressContext.ComputerToNas).Address;
        Assert.Equal(proxyRequired, computer is null);
        if (!proxyRequired)
            Assert.Equal("http://192.0.2.53:19080/", computer!.AbsoluteUri);
        Assert.Equal($"http://{haHost}:19080/", plan.Endpoints.Single(endpoint => endpoint.Context == PlannedAddressContext.HomeAssistantToHost).Address!.AbsoluteUri);
        Assert.Equal("http://qbittorrent:9080/", plan.Endpoints.Single(endpoint => endpoint.Context == PlannedAddressContext.ComposeToService).Address!.AbsoluteUri);
        Assert.Equal(proxyRequired, plan.HasBlockingChecks);
        Assert.Contains(plan.Publications, item => item.Protocol == PlannedPortProtocol.Tcp && item.HostPort == 19080 && item.ContainerPort == 9080);
        Assert.Contains(plan.Publications, item => item.Protocol == PlannedPortProtocol.Udp && item.HostPort == 16881 && item.ContainerPort == 16881);
    }

    [Fact]
    public async Task SpaceRequirementsCombineBudgetsOnlyForTheSameVolume()
    {
        var fixture = new PlanData();
        var same = (await fixture.Compiler.CompileAsync(fixture.Request(dataBytes: 1024, configurationBytes: 512), PreflightData.Pin, Token)).Value!;
        Assert.Equal(1536, Assert.Single(same.VolumeRequirements).MinimumFreeBytes);
        fixture.Layout = new("/volume2/data", PathData.Layout.ConfigurationRoot);
        fixture.HostFacts = PreflightData.Facts(resources: PreflightData.Resources(volumes: [PreflightData.Volume1, new("/volume2", 1000000, 2000)]));
        var separate = (await fixture.Compiler.CompileAsync(fixture.Request(dataBytes: 1024, configurationBytes: 512), PreflightData.Pin, Token)).Value!;
        Assert.Equal(2, separate.VolumeRequirements.Count);
        Assert.Contains(separate.VolumeRequirements, item => item.VolumeMount == "/volume2" && item.MinimumFreeBytes == 1024);
        Assert.Contains(separate.VolumeRequirements, item => item.VolumeMount == "/volume1" && item.MinimumFreeBytes == 512);
        Assert.False(separate.HasBlockingChecks);
    }

    [Theory]
    [InlineData("listener")]
    [InlineData("containers")]
    [InlineData("space")]
    [InlineData("redirected")]
    [InlineData("foreign")]
    [InlineData("unmarked")]
    public async Task FreshBlockingFactsRemainVisibleInThePreview(string problem)
    {
        var fixture = new PlanData();
        switch (problem)
        {
            case "listener":
                fixture.HostFacts = PreflightData.Facts(resources: PreflightData.Resources([19080]));
                break;
            case "containers":
                fixture.HostFacts = PreflightData.Facts(ids: [new string('a', 64)]);
                break;
            case "space":
                fixture.HostFacts = PreflightData.Facts(resources: PreflightData.Resources(volumes: [PreflightData.Volume1 with { AvailableBytes = 1 }]));
                break;
            case "redirected":
                fixture.PathOutput = PathData.Output(media: "entry=directory:0:redirected:41:1026:100:755:empty");
                break;
            case "foreign":
                fixture.Ownership = new(OwnershipMarkerState.Present, new(1, Guid.NewGuid(), fixture.Profile.Definition.ResearchFingerprint));
                break;
            case "unmarked":
                fixture.PathOutput = PathData.Output(configuration: "entry=directory:0:same:42:1026:100:755:not-empty");
                break;
        }
        var plan = (await fixture.Compiler.CompileAsync(fixture.Request(webPort: 19080), PreflightData.Pin, Token)).Value!;
        Assert.True(plan.HasBlockingChecks);
        Assert.Contains(plan.Checks, check => check.Status == PreflightCheckStatus.Blocking);
    }

    [Theory]
    [InlineData("listener")]
    [InlineData("path")]
    [InlineData("space-below-budget")]
    [InlineData("owner")]
    public async Task FreshRevalidationInvalidatesThePreviousFingerprintOnMaterialChanges(string change)
    {
        var fixture = new PlanData();
        var previous = (await fixture.Compiler.CompileAsync(fixture.Request(), PreflightData.Pin, Token)).Value!;
        switch (change)
        {
            case "listener":
                fixture.HostFacts = PreflightData.Facts(resources: PreflightData.Resources([19080]));
                break;
            case "path":
                fixture.PathOutput = PathData.Output(media: "entry=directory:0:same:41:1026:100:755:not-empty");
                break;
            case "space-below-budget":
                fixture.HostFacts = PreflightData.Facts(resources: PreflightData.Resources(volumes: [PreflightData.Volume1 with { AvailableBytes = 1 }]));
                break;
            case "owner":
                fixture.Ownership = new(OwnershipMarkerState.Present, new(1, Guid.NewGuid(), fixture.Profile.Definition.ResearchFingerprint));
                break;
        }
        var rechecked = await fixture.Compiler.RevalidateAsync(previous, PreflightData.Pin, Token);
        Assert.False(rechecked.Unchanged);
        Assert.NotNull(rechecked.Fresh.Value);
        Assert.Equal(2, fixture.Host.Calls);
        Assert.Equal(2, fixture.Paths.Calls);
    }

    [Fact]
    public async Task FreshUnchangedEvidenceRenewsThePreviewWithoutDependingOnTimestampsOrFreeSpaceNoise()
    {
        var fixture = new PlanData();
        var previous = (await fixture.Compiler.CompileAsync(fixture.Request(), PreflightData.Pin, Token)).Value!;
        fixture.Clock.Now += TimeSpan.FromSeconds(10);
        fixture.HostFacts = PreflightData.Facts(serverTime: fixture.Clock.Now,
            resources: PreflightData.Resources(volumes: [PreflightData.Volume1 with { AvailableBytes = 100000 }]));
        var renewed = await fixture.Compiler.RevalidateAsync(previous, PreflightData.Pin, Token);
        Assert.True(renewed.Unchanged);
        Assert.NotEqual(previous.ObservedAt, renewed.Fresh.Value!.ObservedAt);
    }

    [Theory]
    [InlineData("host-profile")]
    [InlineData("host-key")]
    [InlineData("host-principal")]
    [InlineData("path-profile")]
    [InlineData("path-principal")]
    [InlineData("layout")]
    public async Task SnapshotIdentityMismatchNeverBecomesAnInstallablePlan(string change)
    {
        var fixture = new PlanData { Change = change };
        var result = await fixture.Compiler.CompileAsync(fixture.Request(), PreflightData.Pin, Token);
        Assert.Null(result.Value);
        Assert.Equal(OperationErrorCode.IdentityMismatch, result.Failure!.Code);
        if (change.StartsWith("host-", StringComparison.Ordinal))
            Assert.Equal(0, fixture.Paths.Calls);
    }

    [Theory]
    [InlineData("stale-host")]
    [InlineData("future-path")]
    [InlineData("old-path")]
    public async Task StaleOrIncoherentObservationWindowsAreRejected(string change)
    {
        var fixture = new PlanData { Change = change };
        var result = await fixture.Compiler.CompileAsync(fixture.Request(), PreflightData.Pin, Token);
        Assert.Null(result.Value);
        Assert.Equal(OperationErrorCode.ObservationChanged, result.Failure!.Code);
    }

    [Fact]
    public async Task FailedRevalidationDoesNotReuseTheOldSnapshotOrReportUnchanged()
    {
        var fixture = new PlanData();
        var previous = (await fixture.Compiler.CompileAsync(fixture.Request(), PreflightData.Pin, Token)).Value!;
        fixture.Host.Status = SshProbeStatus.TransportFailure;
        var result = await fixture.Compiler.RevalidateAsync(previous, PreflightData.Pin, Token);
        Assert.False(result.Unchanged);
        Assert.Null(result.Fresh.Value);
        Assert.Equal(OperationErrorCode.ReadFailed, result.Fresh.Failure!.Code);
        Assert.Equal(1, fixture.Paths.Calls);
    }

    [Theory]
    [InlineData(SshProbeStatus.AuthenticationRequired, OperationErrorCode.AuthenticationRequired)]
    [InlineData(SshProbeStatus.TimedOut, OperationErrorCode.DeadlineExceeded)]
    [InlineData(SshProbeStatus.InvalidResponse, OperationErrorCode.ResponseInvalid)]
    public async Task ReadFailuresHaveTypedCodesWithoutExposingExceptionText(SshProbeStatus status, OperationErrorCode code)
    {
        var fixture = new PlanData();
        fixture.Host.Status = status;
        var result = await fixture.Compiler.CompileAsync(fixture.Request(), PreflightData.Pin, Token);
        Assert.Equal(code, result.Failure!.Code);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task CallerCancellationAndSecondRunDoNotStartAdditionalCollections()
    {
        var fixture = new PlanData();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        fixture.Host.Wait = true;
        var first = fixture.Compiler.CompileAsync(fixture.Request(), PreflightData.Pin, caller.Token);
        await fixture.Host.Entered.Task.WaitAsync(Token);
        var second = await fixture.Compiler.CompileAsync(fixture.Request(), PreflightData.Pin, Token);
        Assert.Equal(OperationErrorCode.OperationInProgress, second.Failure!.Code);
        caller.Cancel();
        Assert.Equal(OperationOutcomeKind.Cancelled, (await first).Failure!.Kind);
        Assert.Equal(0, fixture.Paths.Calls);
        fixture.Host.Wait = false;
        Assert.NotNull((await fixture.Compiler.CompileAsync(fixture.Request(), PreflightData.Pin, Token)).Value);
    }

    [Fact]
    public async Task UnapprovedEndpointIsRejectedBeforeAnyRemoteRead()
    {
        var fixture = new PlanData();
        var wrongEndpoint = PreflightData.Pin with { EndpointFingerprint = new string('c', 64) };
        var result = await fixture.Compiler.CompileAsync(fixture.Request(), wrongEndpoint, Token);
        Assert.Equal(OperationErrorCode.IdentityMismatch, result.Failure!.Code);
        Assert.Equal(0, fixture.Host.Calls);
        Assert.Equal(0, fixture.Paths.Calls);
    }

    [Fact]
    public async Task TemplateChangesInvalidateThePreviewAndInputModsAreCopied()
    {
        var fixture = new PlanData();
        var previous = (await fixture.Compiler.CompileAsync(fixture.Request(), PreflightData.Pin, Token)).Value!;
        var current = fixture.Profile.Definition;
        var mods = current.DockerMods.ToArray();
        var replacement = new QbittorrentPlanProfile(current.ResearchFingerprint, current.SourceCommit, current.ComposeSha256,
            current.Image, mods, current.ComposePath, current.EnvironmentPath, current.ConfigurationPath,
            current.WatchPath, current.ContainerName, current.ContainerWebUiPort + 1);
        var fingerprint = replacement.Fingerprint;
        mods[0] = "example.invalid/changed:1.0";
        Assert.Equal(current.DockerMods[0], replacement.DockerMods[0]);
        Assert.Equal(fingerprint, replacement.Fingerprint);
        var compiler = new QbittorrentPlanCompiler(new FixedProfile(replacement), fixture.Host, fixture.Paths, fixture.Clock);
        var renewed = await compiler.RevalidateAsync(previous, PreflightData.Pin, Token);
        Assert.NotNull(renewed.Fresh.Value);
        Assert.False(renewed.Unchanged);
    }

    private sealed class FixedProfile(QbittorrentPlanProfile definition) : IQbittorrentPlanProfile
    {
        public QbittorrentPlanProfile Definition => definition;
    }

    [Theory]
    [InlineData("sourceCommit")]
    [InlineData("sourceComposeSha256")]
    [InlineData("schema")]
    [InlineData("unknown")]
    public void EmbeddedTemplateProvenanceAndSchemaAreChecked(string field)
    {
        using var stream = typeof(PinnedQbittorrentPlanProfile).Assembly.GetManifestResourceStream("qbittorrent-plan.json")!;
        using var reader = new StreamReader(stream);
        var data = JsonNode.Parse(reader.ReadToEnd())!;
        if (field == "schema")
            data[field] = 2;
        else
            data[field] = "invalid";
        Assert.ThrowsAny<Exception>(() => PinnedQbittorrentPlanProfile.Parse(data.ToJsonString()));
    }

    [Theory]
    [InlineData("nas")]
    [InlineData("bind")]
    [InlineData("web-port")]
    [InlineData("same-port")]
    [InlineData("network")]
    [InlineData("root-user")]
    [InlineData("umask")]
    [InlineData("timezone")]
    [InlineData("space")]
    public void InvalidChoicesAreRejectedBeforeCollection(string invalid)
    {
        var fixture = new PlanData();
        Assert.Throws<ArgumentException>(() => fixture.Request(
            nas: invalid == "nas" ? "192.0.2.53/path" : "192.0.2.53",
            bind: invalid == "bind" ? "192.0.2.99" : "0.0.0.0",
            webPort: invalid == "web-port" ? 0 : invalid == "same-port" ? 16881 : 19080,
            network: invalid == "network" ? "$(touch bad)" : "media-test",
            userId: invalid == "root-user" ? 0 : 1026,
            umask: invalid == "umask" ? "888" : "002",
            timeZone: invalid == "timezone" ? "../UTC" : "Europe/Moscow",
            dataBytes: invalid == "space" ? long.MaxValue : 1024));
        Assert.Equal(0, fixture.Host.Calls);
    }

    private sealed class PlanData
    {
        internal readonly IQbittorrentPlanProfile Profile = new PinnedQbittorrentPlanProfile();
        internal readonly TestClock Clock = new();
        internal readonly HostProbe Host;
        internal readonly PathsProbe Paths;
        internal readonly QbittorrentPlanCompiler Compiler;
        internal readonly Guid Installation = Guid.Parse("b3d64c85-e391-4960-b58a-c157d74acb62");
        internal MediaPathLayout Layout = PathData.Layout;
        internal NasPreflightFacts HostFacts = PreflightData.Facts();
        internal string PathOutput = PathData.Output();
        internal OwnershipObservation? Ownership;
        internal string? Change;

        internal PlanData()
        {
            Host = new HostProbe(this);
            Paths = new PathsProbe(this);
            Compiler = new(Profile, Host, Paths, Clock);
        }

        internal QbittorrentInstallationRequest Request(string nas = "192.0.2.53", string bind = "0.0.0.0", int webPort = 19080,
            string network = "media-test", int userId = 1026, string umask = "002", string timeZone = "Europe/Moscow",
            long dataBytes = 1024, long configurationBytes = 512) => new(Installation, PreflightData.Target, Layout,
                nas, bind, webPort, 16881, network, userId, 100, umask, timeZone, dataBytes, configurationBytes);
    }

    private sealed class TestClock : TimeProvider
    {
        internal DateTimeOffset Now = PreflightData.Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class HostProbe(PlanData data) : INasPreflightProbe
    {
        internal int Calls;
        internal SshProbeStatus? Status;
        internal bool Wait;
        internal TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<NasPreflightResult> CollectAsync(SshHostKey? approvedKey, CancellationToken token)
        {
            Calls++;
            if (Wait)
            {
                Entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            if (Status is { } status)
                return NasPreflightResult.Failed(SshProbeResult.Failed(status));
            var target = data.Change == "host-principal" ? PreflightData.Target with { Username = "other-user" } : PreflightData.Target;
            var pin = data.Change == "host-key" ? PreflightData.OtherPin : PreflightData.Pin;
            var profile = data.Change == "host-profile" ? new string('b', 64) : data.Profile.Definition.ResearchFingerprint;
            var now = data.Clock.Now - (data.Change == "stale-host" ? TimeSpan.FromMinutes(3) : TimeSpan.Zero);
            return NasPreflightResult.Observed(new(target, pin, profile, now, now, data.HostFacts));
        }
    }

    private sealed class PathsProbe(PlanData data) : INasPathProbe
    {
        internal int Calls;
        public Task<RemotePathResult> InspectAsync(MediaPathLayout layout, SshHostKey? approvedKey, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++;
            var target = data.Change == "path-principal" ? PreflightData.Target with { Username = "other-user" } : PreflightData.Target;
            var profile = data.Change == "path-profile" ? new string('b', 64) : data.Profile.Definition.ResearchFingerprint;
            var observed = data.Clock.Now + (data.Change == "future-path" ? TimeSpan.FromSeconds(1) : data.Change == "old-path" ? -TimeSpan.FromMinutes(3) : TimeSpan.Zero);
            var facts = NasPathProtocol.Parse(layout, 0, data.PathOutput);
            if (data.Ownership is not null)
                facts = facts with { Ownership = data.Ownership };
            var actualLayout = data.Change == "layout" ? new MediaPathLayout("/volume1/other-data", layout.ConfigurationRoot) : layout;
            return Task.FromResult(RemotePathResult.Observed(new(target, actualLayout, PreflightData.Pin, profile, observed, facts)));
        }
    }
}
