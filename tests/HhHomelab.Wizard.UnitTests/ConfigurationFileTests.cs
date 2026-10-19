using System.Security.Cryptography;
using System.Text;
using HhHomelab.Wizard.Application;
using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using Renci.SshNet.Common;
using Xunit;

namespace HhHomelab.Wizard.UnitTests;

public sealed class ConfigurationFileTests : IDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly byte[] Compose = Encoding.UTF8.GetBytes("services:\n  qbittorrent:\n    image: synthetic\n");

    private readonly string directory = Path.Combine(Path.GetTempPath(), $"wizard-file-test-{Guid.NewGuid():N}");

    private readonly ConfigurationFileRequest request;

    private readonly OperationJournalScope scope;

    public ConfigurationFileTests()
    {
        Directory.CreateDirectory(directory);
        var installation = Guid.NewGuid();
        request = Request(installation, "media/compose.yaml", 0x1A4, Compose);
        scope = new(installation, Guid.NewGuid(), PinnedRuntimeProfile.Fingerprint);
        FileOperationJournal.Create(directory, scope);
    }

    [Theory]
    [InlineData("media/compose.yaml", true)]
    [InlineData("media/.env", true)]
    [InlineData("media/config/qbittorrent/qBittorrent.conf", true)]
    [InlineData("", false)]
    [InlineData("/media/compose.yaml", false)]
    [InlineData("media//compose.yaml", false)]
    [InlineData("media/", false)]
    [InlineData("media/../compose.yaml", false)]
    [InlineData("./compose.yaml", false)]
    [InlineData(".hh-homelab-wizard/installation.json", false)]
    [InlineData("media/конфиг.yaml", false)]
    [InlineData("media/compose yaml", false)]
    [InlineData("a/b/c/d/e/f/g/h/i", false)]
    [InlineData("media/-f", false)]
    [InlineData("-T/compose.yaml", false)]
    [InlineData("media/a-b.yaml", true)]
    public void RelativePathIsAsciiBelowTheRootAndOutsideTheMarkerDirectory(string path, bool valid)
    {
        Assert.Equal(valid, ConfigurationFileRequest.IsRelativePath(path));
        Assert.False(ConfigurationFileRequest.IsRelativePath("media/" + new string('a', 65)));
    }

    [Fact]
    public void RequestBindsServiceOwnerModeAndBoundedContent()
    {
        Assert.True(request.IsValid());
        Assert.False(Request(request.Owner.InstallationId, "media/compose.yaml", 0x1FF, Compose).IsValid());
        var oversized = new byte[ConfigurationFileRequest.MaximumContentBytes + 1];
        var error = Assert.Throws<ArgumentException>(() => Request(request.Owner.InstallationId, "media/compose.yaml", 0x1A4, oversized));
        Assert.Equal("file.content.too-large", error.Message);
        Assert.True(Request(request.Owner.InstallationId, "media/compose.yaml", 0x1A4, new byte[ConfigurationFileRequest.MaximumContentBytes]).IsValid());
        var longPath = new string('a', ConfigurationFileRequest.MaximumPathLength + 1);
        Assert.Equal("file.path.too-long", Assert.Throws<ArgumentException>(() => Request(request.Owner.InstallationId, longPath, 0x1A4, Compose)).Message);
        Assert.True(Request(request.Owner.InstallationId, "media/compose.yaml", 0x1A4, []).IsValid());
        var qbittorrent = request.Identity with { Resource = request.Identity.Resource with { Service = "qbittorrent" } };
        Assert.False(new ConfigurationFileRequest(qbittorrent, request.Layout, request.Owner, request.RelativePath, request.Mode, Compose).IsValid());
        var otherOwner = request.Owner with { InstallationId = Guid.NewGuid() };
        Assert.False(new ConfigurationFileRequest(request.Identity, request.Layout, otherOwner, request.RelativePath, request.Mode, Compose).IsValid());
    }

    [Fact]
    public void DescriptorChangesWithEveryDeliveredFact()
    {
        var installation = request.Owner.InstallationId;
        var hash = request.Descriptor.SpecificationHash;
        Assert.NotEqual(hash, Request(installation, "media/compose.yaml", 0x1A4, Encoding.UTF8.GetBytes("services: {}\n"), request.Identity).Descriptor.SpecificationHash);
        Assert.NotEqual(hash, Request(installation, "media/compose.yaml", 0x180, Compose, request.Identity).Descriptor.SpecificationHash);
        Assert.NotEqual(hash, Request(installation, "media/compose.yml", 0x1A4, Compose, request.Identity).Descriptor.SpecificationHash);
        Assert.Equal(hash, Request(installation, "media/compose.yaml", 0x1A4, Compose, request.Identity).Descriptor.SpecificationHash);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Compose)), request.Sha256);
    }

    [Fact]
    public void ContentIsCopiedSoTheCallerCannotChangeItAfterwards()
    {
        var buffer = (byte[])Compose.Clone();
        var copy = Request(request.Owner.InstallationId, "media/compose.yaml", 0x1A4, buffer, request.Identity);
        buffer[0] = (byte)'X';
        Assert.Equal(request.Descriptor, copy.Descriptor);
        Assert.Equal(Compose, copy.Content.ToArray());
    }

    [Fact]
    public void DisposalZeroesTheOwnedContentButKeepsTheJournalDescriptor()
    {
        var secret = Request(request.Owner.InstallationId, "media/.env", 0x180, Encoding.UTF8.GetBytes("SECRET=synthetic\n"));
        var descriptor = secret.Descriptor;
        secret.Dispose();
        Assert.False(secret.IsValid());
        Assert.Equal(descriptor, secret.Descriptor);
        Assert.Throws<ObjectDisposedException>(() => secret.Content.Length);
    }

    [Fact]
    public async Task TransientPayloadWithTheFileBytesIsZeroedAfterTheSend()
    {
        var nas = new FakeNas();
        var client = new NasConfigurationFileClient(PreflightData.Target, _ => nas.Wire(), TimeSpan.FromSeconds(5));
        Assert.Equal(FileDeliveryOutcome.Created, await client.WriteAsync(request, PreflightData.Pin, Token));
        Assert.NotNull(nas.LastPayload);
        Assert.All(nas.LastPayload!, value => Assert.Equal(0, value));
    }

    [Fact]
    public void WriteRequestCarriesHeaderLinesThenExactlyTheRawBytes()
    {
        var payload = NasFileProtocol.WriteRequest(request);
        var headerLength = payload.Length - Compose.Length;
        var lines = Encoding.UTF8.GetString(payload, 0, headerLength).TrimEnd('\n').Split('\n');
        Assert.Equal(NasFileProtocol.WriteRequestMarker, lines[0]);
        Assert.Equal("root=" + PathData.Layout.ConfigurationRoot, lines[1]);
        Assert.Equal(request.Marker, OwnershipMarkerJson.Parse(Encoding.UTF8.GetBytes(lines[2]["marker=".Length..])));
        Assert.Equal(["path=media/compose.yaml", "mode=644", "size=" + Compose.Length, "sha256=" + request.Sha256, "end"], lines[3..]);
        Assert.Equal(Compose, payload[headerLength..]);
        Assert.DoesNotContain(PathData.Layout.ConfigurationRoot, NasFileProtocol.WriteCommandText);
        Assert.DoesNotContain(PathData.Layout.ConfigurationRoot, NasFileProtocol.ReadCommandText);
        Assert.Equal("600", NasFileProtocol.Octal(0x180));
    }

    [Theory]
    [InlineData(0, "file=created\n", FileDeliveryOutcome.Created)]
    [InlineData(0, "file=already-present\n", FileDeliveryOutcome.AlreadyPresent)]
    [InlineData(3, "", FileDeliveryOutcome.Rejected)]
    [InlineData(2, "", FileDeliveryOutcome.Rejected)]
    [InlineData(3, "file=created\n", FileDeliveryOutcome.Unknown)]
    [InlineData(1, "", FileDeliveryOutcome.Unknown)]
    [InlineData(null, "", FileDeliveryOutcome.Unknown)]
    [InlineData(0, "", FileDeliveryOutcome.Unknown)]
    [InlineData(0, "file=created\nextra\n", FileDeliveryOutcome.Unknown)]
    public void WriteStatusIsAcknowledgedOnlyForExactSuccessAndRejectedOnlyBeforeChanges(int? exitCode, string output, FileDeliveryOutcome expected)
    {
        Assert.Equal(expected, NasFileProtocol.ParseWrite(exitCode, output));
    }

    [Fact]
    public void ReadObservationIsParsedOnlyFromTheExactShape()
    {
        var digest = new string('a', 64);
        var present = NasFileProtocol.ParseRead(0, Read("same", "present:12:600:" + digest));
        Assert.Equal(new RemoteFileObservation(RemoteMarkerMatch.Same, RemoteFileState.Present, 12, 0x180, digest), present);
        Assert.Equal(RemoteFileState.Absent, NasFileProtocol.ParseRead(0, Read("same", "absent")).File);
        Assert.Equal(RemoteFileState.Redirected, NasFileProtocol.ParseRead(0, Read("same", "redirected")).File);
        Assert.Equal(new RemoteFileObservation(RemoteMarkerMatch.Different, RemoteFileState.NotChecked), NasFileProtocol.ParseRead(0, Read("different", "not-checked")));
        Assert.NotEqual(present.Revision, NasFileProtocol.ParseRead(0, Read("same", "present:12:644:" + digest)).Revision);
        string[] malformed =
        [
            Read("different", "absent"),
            Read("same", "not-checked"),
            Read("same", "present:12:600:" + digest.ToUpperInvariant()),
            Read("same", "present:12:800:" + digest),
            Read("same", "present:70000:600:" + digest),
            Read("same", "present:-1:600:" + digest),
            Read("unknown", "absent"),
            Read("same", "absent") + "extra\n",
            "hh-homelab-wizard:nas-file:v2\nmarker=same\nfile=absent\nend\n",
        ];
        foreach (var output in malformed)
            Assert.Throws<InvalidDataException>(() => NasFileProtocol.ParseRead(0, output));
        Assert.Throws<InvalidDataException>(() => NasFileProtocol.ParseRead(2, ""));
    }

    [Fact]
    public async Task NewFileIsCreatedOnceAndAResumedRunOnlyObserves()
    {
        var nas = new FakeNas();
        Assert.Equal(OperationOutcomeKind.Applied, (await RunAsync(nas)).Kind);
        Assert.Equal(1, nas.Writes);
        Assert.Equal(Compose, nas.File);
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
        Assert.Equal(OperationOutcomeKind.Applied, (await RunAsync(nas)).Kind);
        Assert.Equal(1, nas.Writes);
        Assert.Equal(OperationOutcomeKind.AlreadySatisfied, (await RunAsync(nas)).Kind);
    }

    [Fact]
    public async Task LostChannelWithoutChangeIsUnknownThenTheConditionalWriteCompletesUnderTheSameIntent()
    {
        var nas = new FakeNas { FailAfterSendWithoutWriting = true };
        Assert.Equal(OperationOutcomeKind.Unknown, (await RunAsync(nas)).Kind);
        nas.FailAfterSendWithoutWriting = false;
        Assert.Equal(OperationOutcomeKind.Applied, (await RunAsync(nas)).Kind);
        Assert.Equal(1, nas.Writes);
    }

    [Theory]
    [InlineData("manual-change")]
    [InlineData("mode")]
    [InlineData("foreign-marker")]
    [InlineData("no-marker")]
    [InlineData("redirected")]
    public async Task ExistingOtherFileOrARootOfAnotherInstallationIsNeverWritten(string state)
    {
        var nas = new FakeNas();
        if (state == "manual-change")
            nas.File = Encoding.UTF8.GetBytes("services: {}\n");
        if (state == "mode")
        {
            nas.File = Compose;
            nas.Mode = 0x180;
        }
        if (state == "foreign-marker")
            nas.Marker = RemoteMarkerMatch.Different;
        if (state == "no-marker")
            nas.Marker = RemoteMarkerMatch.Absent;
        if (state == "redirected")
            nas.Redirected = true;
        var result = await RunAsync(nas);
        Assert.Equal(OperationOutcomeKind.Conflict, result.Kind);
        Assert.Equal(0, nas.Writes);
    }

    [Fact]
    public async Task ManualChangeAfterCreationIsReportedAndNotOverwrittenOnResume()
    {
        var nas = new FakeNas();
        Assert.Equal(OperationOutcomeKind.Applied, (await RunAsync(nas)).Kind);
        nas.File = Encoding.UTF8.GetBytes("services:\n  qbittorrent: {}\n# edited by hand\n");
        var resumed = await RunAsync(nas);
        Assert.Equal(OperationOutcomeKind.Conflict, resumed.Kind);
        Assert.Equal(1, nas.Writes);
        Assert.Equal("services:\n  qbittorrent: {}\n# edited by hand\n", Encoding.UTF8.GetString(nas.File!));
    }

    [Fact]
    public async Task ProvenRejectionChangedHostKeyAndConnectionFailureBeforeSendAreNotUnknown()
    {
        var rejecting = new FakeNas { WriteOutcome = (3, "") };
        var rejected = await RunAsync(rejecting);
        Assert.Equal(OperationOutcomeKind.Rejected, rejected.Kind);
        Assert.Equal(OperationErrorCode.WriteRejected, rejected.Code);

        using var keyDirectory = new FileJournalDirectory(scope);
        var changedKey = new FakeNas { ChangeKeyOnWrite = true };
        var conflict = await RunAsync(changedKey, keyDirectory.Path);
        Assert.Equal(OperationOutcomeKind.Conflict, conflict.Kind);
        Assert.Equal(OperationErrorCode.IdentityMismatch, conflict.Code);
        Assert.Equal(0, changedKey.Writes);

        using var connectDirectory = new FileJournalDirectory(scope);
        var unreachable = new FakeNas { FailWriteConnect = true };
        var notSent = await RunAsync(unreachable, connectDirectory.Path);
        Assert.Equal(OperationOutcomeKind.Transport, notSent.Kind);
        Assert.Equal(0, unreachable.Writes);
    }

    [Fact]
    public async Task UnreadableMarkerIsNotProvenOwnershipAndMalformedReadIsNotAnObservation()
    {
        var unavailable = new FakeNas { Marker = RemoteMarkerMatch.Unavailable };
        var result = await RunAsync(unavailable);
        Assert.False(result.IsSuccess);
        Assert.Equal(0, unavailable.Writes);

        using var otherDirectory = new FileJournalDirectory(scope);
        var malformed = new FakeNas { MalformedRead = true };
        var invalid = await RunAsync(malformed, otherDirectory.Path);
        // The ownership check runs first: an unparsable answer never proves this installation's root.
        Assert.Equal(OperationOutcomeKind.Invalid, invalid.Kind);
        Assert.Equal(OperationErrorCode.OwnershipNotProven, invalid.Code);
        Assert.Equal(0, malformed.Writes);
    }

    [Fact]
    public void OperationMustNameThePinnedEndpointAndThisBuildProfile()
    {
        var client = new NasConfigurationFileClient(PreflightData.Target, _ => new FakeNas().Wire(), TimeSpan.FromSeconds(5));
        var otherNas = request.Identity with { Resource = request.Identity.Resource with { EndpointFingerprint = new string('d', 64) } };
        var mismatched = new ConfigurationFileRequest(otherNas, request.Layout, request.Owner, request.RelativePath, request.Mode, Compose);
        Assert.Equal("file.endpoint.mismatch", Assert.Throws<ArgumentException>(() => new NasConfigurationFileOperation(mismatched, client, PreflightData.Pin)).Message);

        var installation = Guid.NewGuid();
        var otherProfile = new string('e', 64);
        var resource = new ResourceIdentity(installation, Guid.NewGuid(), "nas", otherProfile, PreflightData.Target.EndpointFingerprint);
        var foreignProfile = new ConfigurationFileRequest(new(Guid.NewGuid(), resource), PathData.Layout, new(installation, otherProfile),
            "media/compose.yaml", 0x1A4, Compose);
        Assert.Equal("file.profile.mismatch", Assert.Throws<ArgumentException>(() => new NasConfigurationFileOperation(foreignProfile, client, PreflightData.Pin)).Message);
    }

    private static ConfigurationFileRequest Request(Guid installation, string path, int mode, byte[] content, OperationIdentity? identity = null)
    {
        var resource = new ResourceIdentity(installation, Guid.NewGuid(), "nas", PinnedRuntimeProfile.Fingerprint,
            PreflightData.Target.EndpointFingerprint);
        return new(identity ?? new(Guid.NewGuid(), resource), PathData.Layout, new(installation, PinnedRuntimeProfile.Fingerprint),
            path, mode, content);
    }

    private static string Read(string marker, string file) =>
        "hh-homelab-wizard:nas-file:v1\nmarker=" + marker + "\nfile=" + file + "\nend\n";

    private async Task<OperationResult> RunAsync(FakeNas nas, string? journalDirectory = null)
    {
        var journal = FileOperationJournal.Open(journalDirectory ?? directory, scope);
        var client = new NasConfigurationFileClient(PreflightData.Target, _ => nas.Wire(), TimeSpan.FromSeconds(5));
        var operation = new NasConfigurationFileOperation(request, client, PreflightData.Pin);
        return await new ConfigurationOperationScenario(journal, operation).ExecuteAsync(operation, Token);
    }

    public void Dispose()
    {
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (Path.GetDirectoryName(Path.GetFullPath(directory)) != parent)
            throw new InvalidOperationException("test.directory.invalid");
        Directory.Delete(directory, recursive: true);
    }

    private sealed class FileJournalDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"wizard-file-test-{Guid.NewGuid():N}");

        public FileJournalDirectory(OperationJournalScope scope)
        {
            Directory.CreateDirectory(Path);
            FileOperationJournal.Create(Path, scope);
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    /// <summary>Configuration root of a synthetic NAS shared by every fresh wire. It follows the same
    /// rules as the fixed scripts: exact marker, no replacement, no write behind a redirect.</summary>
    private sealed class FakeNas
    {
        public RemoteMarkerMatch Marker { get; set; } = RemoteMarkerMatch.Same;

        public byte[]? File { get; set; }

        public int Mode { get; set; } = 0x1A4;

        public bool Redirected { get; set; }

        public bool MalformedRead { get; set; }

        public bool LoseResponseAfterWrite { get; set; }

        public bool FailAfterSendWithoutWriting { get; set; }

        public bool FailWriteConnect { get; set; }

        public bool ChangeKeyOnWrite { get; set; }

        public (int? ExitCode, string Output)? WriteOutcome { get; set; }

        public int Writes { get; private set; }

        public byte[]? LastPayload { get; private set; }

        public INasPathWire Wire() => new FakeWire(this);

        private string FileLine()
        {
            if (Redirected)
                return "redirected";
            if (File is null)
                return "absent";
            var digest = Convert.ToHexStringLower(SHA256.HashData(File));
            return "present:" + File.Length + ":" + Convert.ToString(Mode, 8) + ":" + digest;
        }

        private sealed class FakeWire(FakeNas nas) : INasPathWire
        {
            private Func<string, byte[], bool>? accept;

            public Task ConnectAsync(Func<string, byte[], bool> acceptKey, CancellationToken token)
            {
                accept = acceptKey;
                if (!acceptKey("ssh-ed25519", PreflightData.PublicKey))
                    throw new SshConnectionException("synthetic-host-key-refused");
                return Task.CompletedTask;
            }

            public Task<(int? ExitCode, string Output)> ReadConfigurationFileAsync(string request, CancellationToken token)
            {
                Assert.StartsWith(NasFileProtocol.ReadRequestMarker + "\n", request, StringComparison.Ordinal);
                if (nas.MalformedRead)
                    return Task.FromResult<(int?, string)>((0, "invalid\n"));
                var marker = nas.Marker.ToString().ToLowerInvariant();
                var file = nas.Marker == RemoteMarkerMatch.Same ? nas.FileLine() : "not-checked";
                return Task.FromResult<(int?, string)>((0, Read(marker, file)));
            }

            public Task<(int? ExitCode, string Output)> WriteConfigurationFileAsync(byte[] request, Action sending, CancellationToken token)
            {
                if (nas.FailWriteConnect)
                    throw new SshConnectionException("synthetic-connect-failure");
                // Models a key change seen only by the write's own connection: the client refuses it.
                if (nas.ChangeKeyOnWrite && !accept!("ssh-ed25519", [1, 2, 3]))
                    throw new SshConnectionException("synthetic-host-key-changed");
                sending();
                nas.LastPayload = request;
                if (nas.WriteOutcome is { } outcome)
                    return Task.FromResult(outcome);
                if (nas.FailAfterSendWithoutWriting)
                    throw new SshConnectionException("synthetic-lost-channel");
                if (nas.Marker != RemoteMarkerMatch.Same || nas.Redirected || nas.File is not null)
                    return Task.FromResult<(int?, string)>((3, ""));
                nas.Writes++;
                nas.File = request[^Compose.Length..].ToArray();
                nas.Mode = 0x1A4;
                if (nas.LoseResponseAfterWrite)
                    throw new SshConnectionException("synthetic-lost-response");
                return Task.FromResult<(int?, string)>((0, "file=created\n"));
            }

            public Task<RemotePathFacts> ReadPathsAsync(MediaPathLayout layout, CancellationToken token) =>
                throw new InvalidOperationException("test.file-only");

            public Task<MarkerWriteOutcome> WriteOwnershipMarkerAsync(OwnershipMarkerRequest request, Action sending, CancellationToken token) =>
                throw new InvalidOperationException("test.file-only");

            public Task<(int? ExitCode, string Output)> RunLockAsync(string request, Action sending, CancellationToken token) =>
                throw new InvalidOperationException("test.file-only");

            public void Dispose()
            {
            }
        }
    }
}
