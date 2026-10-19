using HhHomelab.Wizard.Application;
using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using Renci.SshNet.Common;
using Xunit;

namespace HhHomelab.Wizard.UnitTests;

public sealed class ConfigurationDirectoryTests : IDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly string directory = Path.Combine(Path.GetTempPath(), $"wizard-directory-test-{Guid.NewGuid():N}");

    private readonly ConfigurationDirectoryRequest request;

    private readonly OperationJournalScope scope;

    public ConfigurationDirectoryTests()
    {
        Directory.CreateDirectory(directory);
        var installation = Guid.NewGuid();
        request = Request(installation, "media/config/qbittorrent", 0x1E8);
        scope = new(installation, Guid.NewGuid(), PinnedRuntimeProfile.Fingerprint);
        FileOperationJournal.Create(directory, scope);
    }

    [Theory]
    [InlineData("media/config/qbittorrent", true)]
    [InlineData("media/watch", true)]
    [InlineData("media/-p", false)]
    [InlineData("-m/x", false)]
    [InlineData("media/../x", false)]
    [InlineData(".hh-homelab-wizard/lock", false)]
    [InlineData("/media", false)]
    [InlineData("media/конфиг", false)]
    public void PathFollowsTheFileRulesAndHasNoDashLeadingComponent(string path, bool valid)
    {
        Assert.Equal(valid, ConfigurationDirectoryRequest.IsRelativePath(path));
    }

    [Fact]
    public void RequestBindsModeServiceAndOwnerAndRejectsLongPathsBeforeHashing()
    {
        Assert.True(request.IsValid());
        Assert.False(Request(request.Owner.InstallationId, "media/watch", 0x1FF).IsValid());
        Assert.True(Request(request.Owner.InstallationId, "media/watch", 0x1C0).IsValid());
        var qbittorrent = request.Identity with { Resource = request.Identity.Resource with { Service = "qbittorrent" } };
        Assert.False(new ConfigurationDirectoryRequest(qbittorrent, request.Layout, request.Owner, request.RelativePath, request.Mode).IsValid());
        Assert.Equal("directory.path.too-long", Assert.Throws<ArgumentException>(() =>
            Request(request.Owner.InstallationId, new string('a', 256), 0x1ED)).Message);
        Assert.NotEqual(request.Descriptor.SpecificationHash,
            new ConfigurationDirectoryRequest(request.Identity, request.Layout, request.Owner, request.RelativePath, 0x1ED).Descriptor.SpecificationHash);
    }

    [Fact]
    public void ProtocolCarriesOnlyStdinParametersAndTrustsOnlyExactOutput()
    {
        var lines = NasDirectoryProtocol.CreateRequest(request).TrimEnd('\n').Split('\n');
        Assert.Equal(NasDirectoryProtocol.CreateRequestMarker, lines[0]);
        Assert.Equal(["path=media/config/qbittorrent", "mode=750", "end"], lines[3..]);
        Assert.DoesNotContain(PathData.Layout.ConfigurationRoot, NasDirectoryProtocol.CreateCommandText);
        Assert.DoesNotContain(PathData.Layout.ConfigurationRoot, NasDirectoryProtocol.ReadCommandText);
        Assert.Equal(DirectoryCreateOutcome.Created, NasDirectoryProtocol.ParseCreate(0, "directory=created\n"));
        Assert.Equal(DirectoryCreateOutcome.AlreadyPresent, NasDirectoryProtocol.ParseCreate(0, "directory=already-present\n"));
        Assert.Equal(DirectoryCreateOutcome.Rejected, NasDirectoryProtocol.ParseCreate(3, ""));
        Assert.Equal(DirectoryCreateOutcome.Unknown, NasDirectoryProtocol.ParseCreate(3, "directory=created\n"));
        Assert.Equal(DirectoryCreateOutcome.Unknown, NasDirectoryProtocol.ParseCreate(1, ""));
        Assert.Equal(DirectoryCreateOutcome.Unknown, NasDirectoryProtocol.ParseCreate(null, ""));
        Assert.Equal(new RemoteDirectoryObservation(RemoteMarkerMatch.Same, RemoteDirectoryState.Present, 0x1E8, true),
            NasDirectoryProtocol.ParseRead(0, Read("same", "present:750:own")));
        var inherited = NasDirectoryProtocol.ParseRead(0, Read("same", "present:2750:other"));
        Assert.False(inherited.OwnedBySshUser);
        Assert.True(inherited.HasPermissions(0x1E8));
        Assert.Equal(RemoteDirectoryState.NotDirectory, NasDirectoryProtocol.ParseRead(0, Read("same", "not-directory")).Directory);
        string[] malformed = [Read("different", "absent"), Read("same", "not-checked"), Read("same", "present:9:own"), Read("same", "present::own"),
            Read("same", "present:750"), Read("same", "present:750:root"), Read("same", "absent") + "x\n"];
        foreach (var output in malformed)
            Assert.Throws<InvalidDataException>(() => NasDirectoryProtocol.ParseRead(0, output));
    }

    [Fact]
    public async Task MissingDirectoryIsCreatedOnceAndAResumedRunOnlyObserves()
    {
        var nas = new FakeNas();
        Assert.Equal(OperationOutcomeKind.Applied, (await RunAsync(nas)).Kind);
        Assert.Equal(1, nas.Creates);
        Assert.Equal(0x1E8, nas.Mode);
        Assert.Equal(OperationOutcomeKind.AlreadySatisfied, (await RunAsync(nas)).Kind);
        Assert.Equal(1, nas.Creates);
    }

    [Fact]
    public async Task SetgidInheritedFromAParentShareStillSatisfiesTheRequestedPermissions()
    {
        var nas = new FakeNas { Mode = 0x5E8 };
        Assert.Equal(OperationOutcomeKind.AlreadySatisfied, (await RunAsync(nas)).Kind);
        Assert.Equal(0, nas.Creates);
    }

    [Fact]
    public async Task LostResponseIsUnknownAndANewProcessConfirmsByObservation()
    {
        var nas = new FakeNas { LoseResponse = true };
        var lost = await RunAsync(nas);
        Assert.Equal(OperationOutcomeKind.Unknown, lost.Kind);
        nas.LoseResponse = false;
        Assert.Equal(OperationOutcomeKind.Applied, (await RunAsync(nas)).Kind);
        Assert.Equal(1, nas.Creates);
    }

    [Theory]
    [InlineData("other-mode")]
    [InlineData("not-directory")]
    [InlineData("redirected")]
    [InlineData("foreign")]
    [InlineData("unmarked")]
    [InlineData("created-by-docker-as-root")]
    public async Task AnythingButTheExactDirectoryUnderTheOwnMarkerIsNeverChanged(string state)
    {
        var nas = new FakeNas();
        switch (state)
        {
            case "other-mode":
                nas.Mode = 0x1ED;
                break;
            case "not-directory":
                nas.State = "not-directory";
                break;
            case "redirected":
                nas.State = "redirected";
                break;
            case "foreign":
                nas.Marker = "different";
                break;
            case "unmarked":
                nas.Marker = "absent";
                break;
            case "created-by-docker-as-root":
                nas.Mode = 0x1E8;
                nas.OwnedBySshUser = false;
                break;
        }
        var result = await RunAsync(nas);
        Assert.Equal(OperationOutcomeKind.Conflict, result.Kind);
        Assert.Equal(0, nas.Creates);
    }

    [Fact]
    public async Task ProvenRejectionChangedKeyAndConnectFailureAreNotUnknown()
    {
        var rejecting = new FakeNas { CreateOutcome = (3, "") };
        Assert.Equal(OperationOutcomeKind.Rejected, (await RunAsync(rejecting)).Kind);

        using var keyDirectory = new JournalDirectory(scope);
        var changedKey = new FakeNas { ChangeKeyOnCreate = true };
        var conflict = await RunAsync(changedKey, keyDirectory.Path);
        Assert.Equal(OperationErrorCode.IdentityMismatch, conflict.Code);
        Assert.Equal(0, changedKey.Creates);

        using var connectDirectory = new JournalDirectory(scope);
        var unreachable = new FakeNas { FailCreateConnect = true };
        Assert.Equal(OperationOutcomeKind.Transport, (await RunAsync(unreachable, connectDirectory.Path)).Kind);
    }

    [Fact]
    public void OperationMustNameThePinnedEndpointAndThisBuildProfile()
    {
        var client = new NasConfigurationDirectoryClient(PreflightData.Target, _ => new FakeNas().Wire(), TimeSpan.FromSeconds(5));
        var otherNas = request.Identity with { Resource = request.Identity.Resource with { EndpointFingerprint = new string('d', 64) } };
        var mismatched = new ConfigurationDirectoryRequest(otherNas, request.Layout, request.Owner, request.RelativePath, request.Mode);
        Assert.Equal("directory.endpoint.mismatch", Assert.Throws<ArgumentException>(() =>
            new NasConfigurationDirectoryOperation(mismatched, client, PreflightData.Pin)).Message);
        var installation = Guid.NewGuid();
        var otherProfile = new string('e', 64);
        var resource = new ResourceIdentity(installation, Guid.NewGuid(), "nas", otherProfile, PreflightData.Target.EndpointFingerprint);
        var foreignProfile = new ConfigurationDirectoryRequest(new(Guid.NewGuid(), resource), PathData.Layout, new(installation, otherProfile), "media/watch", 0x1ED);
        Assert.Equal("directory.profile.mismatch", Assert.Throws<ArgumentException>(() =>
            new NasConfigurationDirectoryOperation(foreignProfile, client, PreflightData.Pin)).Message);
    }

    private static ConfigurationDirectoryRequest Request(Guid installation, string path, int mode)
    {
        var resource = new ResourceIdentity(installation, Guid.NewGuid(), "nas", PinnedRuntimeProfile.Fingerprint,
            PreflightData.Target.EndpointFingerprint);
        return new(new(Guid.NewGuid(), resource), PathData.Layout, new(installation, PinnedRuntimeProfile.Fingerprint), path, mode);
    }

    private static string Read(string marker, string state) =>
        "hh-homelab-wizard:nas-directory:v1\nmarker=" + marker + "\ndirectory=" + state + "\nend\n";

    private async Task<OperationResult> RunAsync(FakeNas nas, string? journalDirectory = null)
    {
        var journal = FileOperationJournal.Open(journalDirectory ?? directory, scope);
        var client = new NasConfigurationDirectoryClient(PreflightData.Target, _ => nas.Wire(), TimeSpan.FromSeconds(5));
        var operation = new NasConfigurationDirectoryOperation(request, client, PreflightData.Pin);
        return await new ConfigurationOperationScenario(journal, operation).ExecuteAsync(operation, Token);
    }

    public void Dispose()
    {
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (Path.GetDirectoryName(Path.GetFullPath(directory)) != parent)
            throw new InvalidOperationException("test.directory.invalid");
        Directory.Delete(directory, recursive: true);
    }

    private sealed class JournalDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"wizard-directory-test-{Guid.NewGuid():N}");

        public JournalDirectory(OperationJournalScope scope)
        {
            Directory.CreateDirectory(Path);
            FileOperationJournal.Create(Path, scope);
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    /// <summary>One directory below a synthetic root; same rules as the fixed scripts.</summary>
    private sealed class FakeNas
    {
        public string Marker { get; set; } = "same";

        public string? State { get; set; }

        public int Mode { get; set; }

        public bool OwnedBySshUser { get; set; } = true;

        public bool LoseResponse { get; set; }

        public bool FailCreateConnect { get; set; }

        public bool ChangeKeyOnCreate { get; set; }

        public (int? ExitCode, string Output)? CreateOutcome { get; set; }

        public int Creates { get; private set; }

        public INasDirectoryWire Wire() => new FakeWire(this);

        private string Line()
        {
            if (Marker != "same")
                return "not-checked";
            if (State is { } state)
                return state;
            return Mode == 0 ? "absent" : "present:" + Convert.ToString(Mode, 8) + (OwnedBySshUser ? ":own" : ":other");
        }

        private sealed class FakeWire(FakeNas nas) : INasDirectoryWire
        {
            private Func<string, byte[], bool>? accept;

            public Task ConnectAsync(Func<string, byte[], bool> acceptKey, CancellationToken token)
            {
                accept = acceptKey;
                if (!acceptKey("ssh-ed25519", PreflightData.PublicKey))
                    throw new SshConnectionException("synthetic-host-key-refused");
                return Task.CompletedTask;
            }

            public Task<(int? ExitCode, string Output)> ReadConfigurationDirectoryAsync(string request, CancellationToken token) =>
                Task.FromResult<(int?, string)>((0, Read(nas.Marker, nas.Line())));

            public Task<(int? ExitCode, string Output)> CreateConfigurationDirectoryAsync(string request, Action sending, CancellationToken token)
            {
                if (nas.FailCreateConnect)
                    throw new SshConnectionException("synthetic-connect-failure");
                if (nas.ChangeKeyOnCreate && !accept!("ssh-ed25519", [1, 2, 3]))
                    throw new SshConnectionException("synthetic-host-key-changed");
                sending();
                if (nas.CreateOutcome is { } outcome)
                    return Task.FromResult(outcome);
                if (nas.Marker != "same" || nas.State is not null || nas.Mode != 0)
                    return Task.FromResult<(int?, string)>((3, ""));
                nas.Creates++;
                nas.Mode = Convert.ToInt32(request.Split('\n')[4]["mode=".Length..], 8);
                if (nas.LoseResponse)
                    throw new SshConnectionException("synthetic-lost-response");
                return Task.FromResult<(int?, string)>((0, "directory=created\n"));
            }

            public void Dispose()
            {
            }
        }
    }
}
