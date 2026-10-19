using System.Text;
using HhHomelab.Wizard.Application;
using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using Renci.SshNet.Common;
using Xunit;

namespace HhHomelab.Wizard.UnitTests;

public sealed class OwnershipMarkerOperationTests : IDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly string directory = Path.Combine(Path.GetTempPath(), $"wizard-marker-test-{Guid.NewGuid():N}");

    private readonly OwnershipMarkerRequest request;

    private readonly OperationJournalScope scope;

    public OwnershipMarkerOperationTests()
    {
        Directory.CreateDirectory(directory);
        var installation = Guid.NewGuid();
        var resource = new ResourceIdentity(installation, Guid.NewGuid(), "nas", PinnedRuntimeProfile.Fingerprint,
            PreflightData.Target.EndpointFingerprint);
        request = new(new(Guid.NewGuid(), resource), PathData.Layout, new(installation, PinnedRuntimeProfile.Fingerprint));
        scope = new(installation, Guid.NewGuid(), PinnedRuntimeProfile.Fingerprint);
        FileOperationJournal.Create(directory, scope);
    }

    [Fact]
    public async Task NewInstallationCreatesTheMarkerOnceAndAResumedRunOnlyObserves()
    {
        var nas = new FakeNas();
        Assert.Equal(OperationOutcomeKind.Applied, (await RunAsync(nas)).Kind);
        Assert.Equal(1, nas.Writes);
        Assert.Equal(OwnershipMarkerJson.Serialize(request.Marker), nas.MarkerJson);
        Assert.Equal(OperationOutcomeKind.AlreadySatisfied, (await RunAsync(nas)).Kind);
        Assert.Equal(1, nas.Writes);
    }

    [Fact]
    public async Task LostResponseAfterSendIsUnknownAndANewProcessConfirmsByObservationWithoutASecondWrite()
    {
        var nas = new FakeNas { LoseResponseAfterWrite = true };
        var first = await RunAsync(nas);
        Assert.Equal(OperationOutcomeKind.Unknown, first.Kind);
        Assert.Equal(OperationErrorCode.WriteUncertain, first.Code);
        nas.LoseResponseAfterWrite = false;
        var resumed = await RunAsync(nas);
        Assert.Equal(OperationOutcomeKind.Applied, resumed.Kind);
        Assert.Equal(1, nas.Writes);
        Assert.Equal(OperationOutcomeKind.AlreadySatisfied, (await RunAsync(nas)).Kind);
    }

    [Fact]
    public async Task LostChannelWithoutChangeIsUnknownThenTheConditionalWriteCompletesUnderTheSameIntent()
    {
        var nas = new FakeNas { FailAfterSendWithoutWriting = true };
        var first = await RunAsync(nas);
        Assert.Equal(OperationOutcomeKind.Unknown, first.Kind);
        Assert.Equal(OperationErrorCode.WriteUncertain, first.Code);
        nas.FailAfterSendWithoutWriting = false;
        Assert.Equal(OperationOutcomeKind.Applied, (await RunAsync(nas)).Kind);
        Assert.Equal(1, nas.Writes);
        Assert.Equal(OperationOutcomeKind.AlreadySatisfied, (await RunAsync(nas)).Kind);
    }

    [Fact]
    public async Task WriteInterruptedAfterStagingIsCompletedWhenTheIntentIsStillPending()
    {
        var nas = new FakeNas { FailAfterSendWithoutWriting = true, StageOnFailure = true };
        Assert.Equal(OperationOutcomeKind.Unknown, (await RunAsync(nas)).Kind);
        Assert.True(nas.Staged);
        nas.FailAfterSendWithoutWriting = false;
        Assert.Equal(OperationOutcomeKind.Applied, (await RunAsync(nas)).Kind);
        Assert.Equal(1, nas.Writes);
        Assert.Equal(OwnershipMarkerJson.Serialize(request.Marker), nas.MarkerJson);
    }

    [Fact]
    public async Task RejectedRepeatedWriteStaysUnknownBecauseTheEarlierSentAttemptIsStillUnsettled()
    {
        var nas = new FakeNas { FailAfterSendWithoutWriting = true };
        Assert.Equal(OperationOutcomeKind.Unknown, (await RunAsync(nas)).Kind);
        nas.FailAfterSendWithoutWriting = false;
        nas.WriteOutcome = MarkerWriteOutcome.Rejected;
        var repeated = await RunAsync(nas);
        Assert.Equal(OperationOutcomeKind.Unknown, repeated.Kind);
        Assert.Equal(OperationErrorCode.WriteRejected, repeated.Code);
        nas.WriteOutcome = null;
        Assert.Equal(OperationOutcomeKind.Applied, (await RunAsync(nas)).Kind);
    }

    [Fact]
    public void ResourceMustNameThePinnedEndpoint()
    {
        var otherNas = request.Identity with { Resource = request.Identity.Resource with { EndpointFingerprint = new string('d', 64) } };
        var mismatched = request with { Identity = otherNas };
        var nas = new FakeNas();
        var probe = new NasPathProbe(PreflightData.Target, _ => nas.Wire(), TimeSpan.FromSeconds(5), TimeProvider.System);
        var writer = new NasOwnershipMarkerWriter(PreflightData.Target, _ => nas.Wire(), TimeSpan.FromSeconds(5));
        var error = Assert.Throws<ArgumentException>(() => new NasOwnershipMarkerOperation(mismatched, probe, writer, PreflightData.Pin));
        Assert.Equal("ownership.endpoint.mismatch", error.Message);
    }

    [Fact]
    public async Task ResourceProfileDifferentFromTheObservedBuildProfileNeverProvesOwnershipOrWrites()
    {
        var installation = Guid.NewGuid();
        var otherProfile = new string('e', 64);
        var resource = new ResourceIdentity(installation, Guid.NewGuid(), "nas", otherProfile, PreflightData.Target.EndpointFingerprint);
        var foreignProfile = new OwnershipMarkerRequest(new(Guid.NewGuid(), resource), PathData.Layout, new(installation, otherProfile));
        var otherScope = new OperationJournalScope(installation, Guid.NewGuid(), otherProfile);
        using var otherDirectory = new MarkerJournalDirectory(otherScope);
        var nas = new FakeNas();
        var result = await RunAsync(nas, otherDirectory.Path, foreignProfile, otherScope);
        Assert.Equal(OperationOutcomeKind.Invalid, result.Kind);
        Assert.Equal(OperationErrorCode.OwnershipNotProven, result.Code);
        Assert.Equal(0, nas.Writes);
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("unmarked")]
    [InlineData("profile")]
    public async Task ExistingContentsOrAnotherMarkerAreNeverWritten(string state)
    {
        var nas = new FakeNas();
        if (state == "foreign")
            nas.MarkerJson = OwnershipMarkerJson.Serialize(new(1, Guid.NewGuid(), PinnedRuntimeProfile.Fingerprint));
        if (state == "unmarked")
            nas.RootHasContents = true;
        if (state == "profile")
            nas.MarkerJson = OwnershipMarkerJson.Serialize(new(1, request.Owner.InstallationId, new string('c', 64)));
        var result = await RunAsync(nas);
        Assert.Equal(OperationOutcomeKind.Conflict, result.Kind);
        Assert.Equal(0, nas.Writes);
    }

    [Fact]
    public async Task StagedLeftoverOfAnInterruptedCreationIsCompleted()
    {
        var nas = new FakeNas { Staged = true };
        Assert.Equal(OperationOutcomeKind.Applied, (await RunAsync(nas)).Kind);
        Assert.Equal(1, nas.Writes);
    }

    [Fact]
    public async Task StagedLeftoverOfAnotherInstallationIsNeverAdopted()
    {
        var other = new OwnershipMarker(1, Guid.NewGuid(), PinnedRuntimeProfile.Fingerprint);
        var nas = new FakeNas { Staged = true, StagedJson = OwnershipMarkerJson.Serialize(other) };
        var result = await RunAsync(nas);
        Assert.Equal(OperationOutcomeKind.Conflict, result.Kind);
        Assert.Equal(0, nas.Writes);
    }

    [Fact]
    public async Task ChangedHostKeyOnTheWriteConnectionIsAnIdentityConflictNotTransport()
    {
        var nas = new FakeNas { ChangeKeyOnWrite = true };
        var writer = new NasOwnershipMarkerWriter(PreflightData.Target, _ => nas.Wire(), TimeSpan.FromSeconds(5));
        Assert.Equal(MarkerWriteOutcome.IdentityChanged, await writer.WriteAsync(request, PreflightData.Pin, Token));
        Assert.Equal(0, nas.Writes);
        nas.ChangeKeyOnWrite = false;
        nas.WriteOutcome = MarkerWriteOutcome.IdentityChanged;
        var result = await RunAsync(nas);
        Assert.Equal(OperationOutcomeKind.Conflict, result.Kind);
        Assert.Equal(OperationErrorCode.IdentityMismatch, result.Code);
    }

    [Fact]
    public async Task ProvenRejectionAndConnectionFailureBeforeSendAreNotUnknown()
    {
        var rejecting = new FakeNas { WriteOutcome = MarkerWriteOutcome.Rejected };
        var rejected = await RunAsync(rejecting);
        Assert.Equal(OperationOutcomeKind.Rejected, rejected.Kind);
        Assert.Equal(OperationErrorCode.WriteRejected, rejected.Code);
        using var otherDirectory = new MarkerJournalDirectory(scope);
        var unreachable = new FakeNas { FailWriteConnect = true };
        var notSent = await RunAsync(unreachable, otherDirectory.Path);
        Assert.Equal(OperationOutcomeKind.Transport, notSent.Kind);
        Assert.Equal(0, unreachable.Writes);
    }

    [Theory]
    [InlineData(0, "marker=created\n", MarkerWriteOutcome.Created)]
    [InlineData(0, "marker=already-present\n", MarkerWriteOutcome.AlreadyPresent)]
    [InlineData(3, "", MarkerWriteOutcome.Rejected)]
    [InlineData(2, "", MarkerWriteOutcome.Rejected)]
    [InlineData(3, "marker=created\n", MarkerWriteOutcome.Unknown)]
    [InlineData(1, "", MarkerWriteOutcome.Unknown)]
    [InlineData(null, "", MarkerWriteOutcome.Unknown)]
    [InlineData(0, "", MarkerWriteOutcome.Unknown)]
    [InlineData(0, "marker=created\nextra\n", MarkerWriteOutcome.Unknown)]
    public void WriteStatusIsAcknowledgedOnlyForExactSuccessAndRejectedOnlyBeforeChanges(int? exitCode, string output, MarkerWriteOutcome expected)
    {
        Assert.Equal(expected, NasMarkerWriteProtocol.Parse(exitCode, output));
    }

    [Fact]
    public void RequestCarriesTheCanonicalMarkerOnStdinAndRoundTrips()
    {
        var lines = NasMarkerWriteProtocol.Request(request).TrimEnd('\n').Split('\n');
        Assert.Equal(NasMarkerWriteProtocol.RequestMarker, lines[0]);
        Assert.Equal("root=" + PathData.Layout.ConfigurationRoot, lines[1]);
        var json = lines[2]["marker=".Length..];
        Assert.Equal(request.Marker, OwnershipMarkerJson.Parse(Encoding.UTF8.GetBytes(json)));
        Assert.Equal("end", lines[3]);
        Assert.DoesNotContain(PathData.Layout.ConfigurationRoot, NasMarkerWriteProtocol.Command);
    }

    [Fact]
    public void RequestMustBindTheNasResourceToTheSameInstallationAndProfile()
    {
        Assert.True(request.IsValid());
        Assert.False((request with { Owner = request.Owner with { InstallationId = Guid.NewGuid() } }).IsValid());
        var qbittorrent = request.Identity with { Resource = request.Identity.Resource with { Service = "qbittorrent" } };
        Assert.False((request with { Identity = qbittorrent }).IsValid());
        Assert.NotEqual(request.Descriptor.SpecificationHash,
            (request with { Layout = new MediaPathLayout("/volume1/data", "/volume1/docker/other") }).Descriptor.SpecificationHash);
    }

    private async Task<OperationResult> RunAsync(FakeNas nas, string? journalDirectory = null,
        OwnershipMarkerRequest? overrideRequest = null, OperationJournalScope? overrideScope = null)
    {
        var journal = FileOperationJournal.Open(journalDirectory ?? directory, overrideScope ?? scope);
        var probe = new NasPathProbe(PreflightData.Target, _ => nas.Wire(), TimeSpan.FromSeconds(5), TimeProvider.System);
        var writer = new NasOwnershipMarkerWriter(PreflightData.Target, _ => nas.Wire(), TimeSpan.FromSeconds(5));
        var operation = new NasOwnershipMarkerOperation(overrideRequest ?? request, probe, writer, PreflightData.Pin);
        return await new ConfigurationOperationScenario(journal, operation).ExecuteAsync(operation, Token);
    }

    public void Dispose()
    {
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (Path.GetDirectoryName(Path.GetFullPath(directory)) != parent)
            throw new InvalidOperationException("test.directory.invalid");
        Directory.Delete(directory, recursive: true);
    }

    private sealed class MarkerJournalDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"wizard-marker-test-{Guid.NewGuid():N}");

        public MarkerJournalDirectory(OperationJournalScope scope)
        {
            Directory.CreateDirectory(Path);
            FileOperationJournal.Create(Path, scope);
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    /// <summary>Configuration root state of a synthetic NAS shared by every fresh wire.</summary>
    private sealed class FakeNas
    {
        public string? MarkerJson { get; set; }

        public bool RootHasContents { get; set; }

        public bool Staged { get; set; }

        public string? StagedJson { get; set; }

        public bool ChangeKeyOnWrite { get; set; }

        public bool LoseResponseAfterWrite { get; set; }

        public bool FailAfterSendWithoutWriting { get; set; }

        public bool FailWriteConnect { get; set; }

        public bool StageOnFailure { get; set; }

        public MarkerWriteOutcome? WriteOutcome { get; set; }

        public int Writes { get; private set; }

        public INasPathWire Wire() => new FakeWire(this);

        private sealed class FakeWire(FakeNas nas) : INasPathWire
        {
            private bool connected;

            private Func<string, byte[], bool>? accept;

            public Task ConnectAsync(Func<string, byte[], bool> acceptKey, CancellationToken token)
            {
                accept = acceptKey;
                if (!acceptKey("ssh-ed25519", PreflightData.PublicKey))
                    throw new SshConnectionException("synthetic-host-key-refused");
                connected = true;
                return Task.CompletedTask;
            }

            public Task<RemotePathFacts> ReadPathsAsync(MediaPathLayout layout, CancellationToken token)
            {
                var root = nas.MarkerJson is not null || nas.RootHasContents || nas.Staged
                    ? "entry=directory:0:same:42:1026:100:755:not-empty"
                    : null;
                var marker = nas switch
                {
                    { MarkerJson: { } json } => "marker=present\nmarker-content=" + Convert.ToHexStringLower(Encoding.UTF8.GetBytes(json)),
                    { Staged: true, StagedJson: { } staged } => "marker=staged\nmarker-content=" + Convert.ToHexStringLower(Encoding.UTF8.GetBytes(staged)),
                    { Staged: true } => "marker=staged",
                    _ => "marker=absent",
                };
                return Task.FromResult(NasPathProtocol.Parse(layout, 0, PathData.Output(configuration: root, marker: marker)));
            }

            public Task<MarkerWriteOutcome> WriteOwnershipMarkerAsync(OwnershipMarkerRequest request, Action sending, CancellationToken token)
            {
                if (!connected || nas.FailWriteConnect)
                    throw new SshConnectionException("synthetic-connect-failure");
                // Models a key change seen only by the write's own connection: the client refuses it.
                if (nas.ChangeKeyOnWrite && !accept!("ssh-ed25519", [1, 2, 3]))
                    throw new SshConnectionException("synthetic-host-key-changed");
                sending();
                if (nas.WriteOutcome is { } outcome)
                    return Task.FromResult(outcome);
                if (nas.FailAfterSendWithoutWriting)
                {
                    nas.Staged = nas.StageOnFailure;
                    nas.StagedJson = nas.StageOnFailure ? OwnershipMarkerJson.Serialize(request.Marker) : null;
                    throw new SshConnectionException("synthetic-lost-channel");
                }
                nas.Writes++;
                nas.Staged = false;
                nas.StagedJson = null;
                nas.MarkerJson = OwnershipMarkerJson.Serialize(request.Marker);
                if (nas.LoseResponseAfterWrite)
                    throw new SshConnectionException("synthetic-lost-response");
                return Task.FromResult(MarkerWriteOutcome.Created);
            }

            public void Dispose()
            {
            }
        }
    }
}
