using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using Renci.SshNet.Common;
using Xunit;

namespace HhHomelab.Wizard.UnitTests;

public sealed class RemotePathTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void LayoutMapsTheSharedDataRootToTheFixedContainerDirectories()
    {
        var layout = PathData.Layout;
        Assert.Equal(8, layout.Entries.Count);
        var movies = layout.Entries.Single(entry => entry.Role == RemotePathRole.TorrentsMovies);
        Assert.Equal("/volume1/data/torrents/movies", movies.HostPath);
        Assert.Equal("/data/torrents/movies", movies.ContainerPath);
        var series = layout.Entries.Single(entry => entry.Role == RemotePathRole.MediaSeries);
        Assert.Equal("/data/media/tv", series.ContainerPath);
        var configuration = layout.Entries[^1];
        Assert.Equal(RemotePathRole.ConfigurationRoot, configuration.Role);
        Assert.Null(configuration.ContainerPath);
    }

    [Theory]
    [InlineData("/volume1/Медиа с пробелом", true)]
    [InlineData("/volume12/data", true)]
    [InlineData("/volume1", false)]
    [InlineData("volume1/data", false)]
    [InlineData("/volumeUSB1/usbshare/data", false)]
    [InlineData("/volume0/data", false)]
    [InlineData("/data", false)]
    [InlineData("/volume1/data/", false)]
    [InlineData("/volume1//data", false)]
    [InlineData("/volume1/./data", false)]
    [InlineData("/volume1/data/../docker", false)]
    [InlineData("/volume1/da\nta", false)]
    [InlineData("/volume1/da\\ta", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("/volume1/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a", true)]
    [InlineData("/volume1/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a", false)]
    public void PathsAreAbsoluteNormalizedAndBelowADsmVolume(string? path, bool valid)
    {
        Assert.Equal(valid, MediaPathLayout.IsVolumePath(path));
    }

    [Theory]
    [InlineData("/volume1/data", "/volume1/data")]
    [InlineData("/volume1/data", "/volume1/data/docker")]
    [InlineData("/volume1/data/media", "/volume1/data")]
    public void OverlappingRootsAreRejected(string data, string configuration)
    {
        var error = Assert.Throws<ArgumentException>(() => new MediaPathLayout(data, configuration));
        Assert.Equal("paths.roots.overlap", error.Message);
    }

    [Fact]
    public void ComponentLimitCountsUtf8BytesNotCharacters()
    {
        Assert.True(MediaPathLayout.IsVolumePath("/volume1/" + new string('ф', 127)));
        Assert.False(MediaPathLayout.IsVolumePath("/volume1/" + new string('ф', 128)));
        Assert.True(MediaPathLayout.IsVolumePath("/volume1/" + new string('a', 200)));
        var unpairedSurrogate = "/volume1/bad" + (char)0xD800 + "name";
        Assert.False(MediaPathLayout.IsVolumePath(unpairedSurrogate));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("/volume1")]
    [InlineData("/volume1/a/../data")]
    public void VolumeExtractionRejectsInvalidPathsWithTheDomainCode(string? path)
    {
        var error = Assert.Throws<ArgumentException>(() => MediaPathLayout.VolumeOf(path));

        Assert.Equal("paths.volume.invalid", error.Message);
        Assert.Equal("/volume12", MediaPathLayout.VolumeOf("/volume12/data"));
    }

    [Fact]
    public void DeepestGeneratedEntryOfAMaximalMissingRootIsStillAValidObservation()
    {
        var root = "/volume1/" + string.Join('/', Enumerable.Repeat("a", MediaPathLayout.MaximumComponents - 1));
        var layout = new MediaPathLayout(root, "/volume1/docker");
        var deepest = layout.Entries.Single(entry => entry.Role == RemotePathRole.TorrentsMovies);
        Assert.Equal(MediaPathLayout.MaximumEntryComponents, deepest.HostPath[1..].Split('/').Length);
        var observation = new RemotePathObservation(RemotePathRole.TorrentsMovies, RemotePathKind.Missing,
            MediaPathLayout.MaximumEntryComponents - 1, RemotePathCanonical.Same, 41, 0, 0, 493, RemotePathContents.NotApplicable);
        Assert.True(observation.IsValid);
        Assert.False((observation with { AncestorDepth = MediaPathLayout.MaximumEntryComponents + 1 }).IsValid);
    }

    [Fact]
    public void LeftoverMountPointOnTheSystemDeviceIsNotAMountedVolume()
    {
        var unmounted = PathData.Output(media: "entry=missing:2:same:2:0:0:755:not-applicable");
        var checks = RemotePathAssessment.Evaluate(PathData.Snapshot(unmounted));
        Assert.Contains(checks, check => check.Code == "paths.media-movies.volume-unmounted" && check.Status == PreflightCheckStatus.Blocking);
        var allOnRoot = PathData.Output().Replace(":41:", ":2:", StringComparison.Ordinal);
        var rootChecks = RemotePathAssessment.Evaluate(PathData.Snapshot(allOnRoot));
        Assert.Contains(rootChecks, check => check.Code == "paths.data-root.volume-unmounted" && check.Status == PreflightCheckStatus.Blocking);
        Assert.DoesNotContain(rootChecks, check => check.Code == "paths.data-root" && check.Status == PreflightCheckStatus.Passed);
    }

    [Fact]
    public void ExistingDirectoryThatCannotBeListedIsBlockedNotPassed()
    {
        var locked = PathData.Output(media: "entry=directory:0:same:41:0:0:700:unavailable");
        var checks = RemotePathAssessment.Evaluate(PathData.Snapshot(locked));
        Assert.Contains(checks, check => check.Code == "paths.media-movies.unavailable" && check.Status == PreflightCheckStatus.Blocking);
        Assert.DoesNotContain(checks, check => check.Code == "paths.media-movies" && check.Status == PreflightCheckStatus.Passed);
    }

    [Fact]
    public void SnapshotBindingRequiresTheSameSshPrincipal()
    {
        var snapshot = PathData.Snapshot(PathData.Output());
        Assert.True(snapshot.IsBoundTo(PreflightData.Target, PreflightData.Pin, PreflightData.Profile));
        Assert.False(snapshot.IsBoundTo(PreflightData.Target with { Username = "root" }, PreflightData.Pin, PreflightData.Profile));
        Assert.False(snapshot.IsBoundTo(PreflightData.Target, PreflightData.OtherPin, PreflightData.Profile));
        Assert.False(snapshot.IsBoundTo(PreflightData.Target, PreflightData.Pin, new string('b', 64)));
    }

    [Fact]
    public void SiblingPrefixIsNotAnOverlap()
    {
        var layout = new MediaPathLayout("/volume1/data", "/volume1/data-config");
        Assert.Equal("/volume1/data-config", layout.ConfigurationRoot);
    }

    [Fact]
    public void RequestCarriesPathsOnlyAsStdinDataInLayoutOrder()
    {
        var request = NasPathProtocol.Request(new MediaPathLayout("/volume1/Медиа $(id)", "/volume1/docker/hh"));
        var lines = request.TrimEnd('\n').Split('\n');
        Assert.Equal(NasPathProtocol.RequestMarker, lines[0]);
        Assert.Equal("path=/volume1/Медиа $(id)", lines[1]);
        Assert.Equal("path=/volume1/docker/hh", lines[^3]);
        Assert.Equal("owner=/volume1/docker/hh", lines[^2]);
        Assert.Equal("end", lines[^1]);
        Assert.DoesNotContain("$(id)", NasPathProtocol.Command);
    }

    [Fact]
    public void FreshLayoutOnOneVolumePassesPathChecksButNotAclOrPermissions()
    {
        var snapshot = PathData.Snapshot(PathData.Output());
        var checks = RemotePathAssessment.Evaluate(snapshot);
        Assert.Contains(checks, check => check.Code == "paths.data-root" && check.Status == PreflightCheckStatus.Passed);
        Assert.Contains(checks, check => check.Code == "paths.torrents-movies" && check.Status == PreflightCheckStatus.Passed);
        Assert.Contains(checks, check => check.Code == "ownership.new-installation" && check.Status == PreflightCheckStatus.Passed);
        Assert.Contains(checks, check => check.Code == "paths.hardlink-device" && check.Status == PreflightCheckStatus.Passed);
        Assert.Contains(checks, check => check.Code == "paths.permissions" && check.Status == PreflightCheckStatus.Unverified);
        Assert.Contains(checks, check => check.Code == "paths.dsm-acl" && check.Status == PreflightCheckStatus.NotChecked);
        var missing = snapshot.Observation(RemotePathRole.TorrentsMovies);
        Assert.Equal(RemotePathKind.Missing, missing.Kind);
        Assert.Equal(2, missing.AncestorDepth);
        Assert.Equal(Convert.ToInt32("755", 8), missing.Mode);
    }

    [Fact]
    public void ExistingMediaLibraryIsAllowedButNonEmptyConfigurationRootRequiresOwnership()
    {
        var output = PathData.Output(media: "entry=directory:0:same:41:1026:100:775:not-empty",
            configuration: "entry=directory:0:same:42:1026:100:755:not-empty");
        var checks = RemotePathAssessment.Evaluate(PathData.Snapshot(output));
        Assert.Contains(checks, check => check.Code == "paths.media-movies" && check.Status == PreflightCheckStatus.Passed);
        Assert.Contains(checks, check => check.Code == "ownership.unmarked-contents" && check.Status == PreflightCheckStatus.Blocking);
        var empty = PathData.Output(configuration: "entry=directory:0:same:42:1026:100:755:empty");
        Assert.Contains(RemotePathAssessment.Evaluate(PathData.Snapshot(empty)),
            check => check.Code == "ownership.new-installation" && check.Status == PreflightCheckStatus.Passed);
    }

    [Theory]
    [InlineData("entry=directory:0:redirected:41:1026:100:755:empty", "paths.media-movies.redirected")]
    [InlineData("entry=missing:1:redirected:41:1026:100:777:not-applicable", "paths.media-movies.redirected")]
    [InlineData("entry=not-directory:0:same:41:1026:100:644:not-applicable", "paths.media-movies.not-directory")]
    [InlineData("entry=not-directory:1:same:41:1026:100:644:not-applicable", "paths.media-movies.not-directory")]
    [InlineData("entry=unavailable:0:unavailable:-:-:-:-:unavailable", "paths.media-movies.unavailable")]
    [InlineData("entry=missing:1:unavailable:41:1026:100:755:not-applicable", "paths.media-movies.unavailable")]
    [InlineData("entry=missing:4:same:41:0:0:755:not-applicable", "paths.media-movies.volume-missing")]
    public void SymlinkFileUnreadableOrMissingVolumeBlocksOnlyTheAffectedPath(string media, string code)
    {
        var checks = RemotePathAssessment.Evaluate(PathData.Snapshot(PathData.Output(media: media)));
        Assert.Contains(checks, check => check.Code == code && check.Status == PreflightCheckStatus.Blocking);
        Assert.Contains(checks, check => check.Code == "paths.data-root" && check.Status == PreflightCheckStatus.Passed);
    }

    [Fact]
    public void VolumeMountItselfMayBeTheExistingAncestor()
    {
        var output = PathData.Output(media: "entry=missing:3:same:41:0:0:755:not-applicable");
        Assert.Contains(RemotePathAssessment.Evaluate(PathData.Snapshot(output)),
            check => check.Code == "paths.media-movies" && check.Status == PreflightCheckStatus.Passed);
    }

    [Fact]
    public void DownloadsAndLibraryOnDifferentDevicesCannotHardlink()
    {
        var output = PathData.Output(media: "entry=directory:0:same:77:1026:100:775:empty");
        var checks = RemotePathAssessment.Evaluate(PathData.Snapshot(output));
        Assert.Contains(checks, check => check.Code == "paths.hardlink-device.mismatch" && check.Status == PreflightCheckStatus.Blocking);
        var unknown = PathData.Output(media: "entry=unavailable:0:unavailable:-:-:-:-:unavailable");
        Assert.Contains(RemotePathAssessment.Evaluate(PathData.Snapshot(unknown)),
            check => check.Code == "paths.hardlink-device.unavailable" && check.Status == PreflightCheckStatus.Blocking);
        var configurationElsewhere = PathData.Output(configuration: "entry=missing:1:same:99:0:0:755:not-applicable");
        Assert.Contains(RemotePathAssessment.Evaluate(PathData.Snapshot(configurationElsewhere)),
            check => check.Code == "paths.hardlink-device" && check.Status == PreflightCheckStatus.Passed);
    }

    [Theory]
    [InlineData("entry=directory:0:same:41:1026:100:755:empty", "entry=directory:0:same:41:1026:100:755:sometimes")]
    [InlineData("entry=directory:0:same:41:1026:100:755:empty", "entry=directory:1:same:41:1026:100:755:empty")]
    [InlineData("entry=directory:0:same:41:1026:100:755:empty", "entry=directory:0:same:41:1026:100:755:not-applicable")]
    [InlineData("entry=directory:0:same:41:1026:100:755:empty", "entry=directory:0:same:41:1026:100:758:empty")]
    [InlineData("entry=directory:0:same:41:1026:100:755:empty", "entry=directory:0:same:41:1026:100:17777:empty")]
    [InlineData("entry=directory:0:same:41:1026:100:755:empty", "entry=directory:0:same:41:1026:100:8:empty")]
    [InlineData("entry=directory:0:same:41:1026:100:755:empty", "entry=directory:0:same:-:1026:100:755:empty")]
    [InlineData("entry=directory:0:same:41:1026:100:755:empty", "entry=directory:0:same:41:1026:100:755")]
    [InlineData("entry=directory:0:same:41:1026:100:755:empty", "entry=directory:0:same:/volume1/data:1026:100:755:empty")]
    [InlineData("entry=directory:0:same:41:1026:100:755:empty", "entry=unavailable:0:unavailable:41:-:-:-:unavailable")]
    [InlineData("entry=directory:0:same:41:1026:100:755:empty", "entry=missing:0:same:41:1026:100:755:not-applicable")]
    [InlineData("entry=directory:0:same:41:1026:100:755:empty", "entry=directory:0:same:41:1026:100:755:empty\nentry=directory:0:same:41:1026:100:755:empty")]
    [InlineData("hh-homelab-wizard:nas-paths:v3", "hh-homelab-wizard:nas-paths:v1")]
    [InlineData("hh-homelab-wizard:nas-paths:v3", "hh-homelab-wizard:nas-paths:v2")]
    [InlineData("hh-homelab-wizard:nas-paths:v3", "hh-homelab-wizard:nas-preflight:v2")]
    [InlineData("marker=absent", "marker=maybe")]
    [InlineData("marker=absent", "marker=present")]
    [InlineData("marker=absent", "marker=absent\nmarker-content=7b7d")]
    [InlineData("marker=absent", "marker=present\nmarker-content=7b7")]
    [InlineData("marker=absent", "marker=present\nmarker-content=7B7D")]
    [InlineData("marker=absent", "marker=present\nmarker-content=7b 7d")]
    [InlineData("marker=absent\n", "")]
    [InlineData("root-device=2", "root-device=")]
    [InlineData("root-device=2", "root-device=-2")]
    [InlineData("root-device=2\n", "")]
    [InlineData("identity=1026:100", "identity=-1:100")]
    [InlineData("identity=1026:100", "identity=1026:-1")]
    [InlineData("identity=1026:100", "identity=2147483648:100")]
    [InlineData("identity=1026:100", "identity=1026:2147483648")]
    [InlineData("identity=1026:100", "identity=1026:100:200")]
    [InlineData("identity=1026:100", "identity=1026")]
    [InlineData("identity=1026:100", "identity=1026: 100")]
    [InlineData("identity=1026:100", "identity=uid:gid")]
    [InlineData("identity=1026:100\n", "")]
    [InlineData("identity=1026:100", "identity=1026:100\nidentity=1026:100")]
    [InlineData("end", "")]
    public void MalformedOrInconsistentEntriesRejectTheWholeResponse(string original, string replacement)
    {
        var output = PathData.Output().Replace(original, replacement, StringComparison.Ordinal);
        var error = Assert.Throws<InvalidDataException>(() => NasPathProtocol.Parse(PathData.Layout, 0, output));
        Assert.Equal("paths.response.invalid", error.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(2)]
    public void FramingFailureExitIsRejectedEvenWithCompleteOutput(int? exitCode)
    {
        Assert.Throws<InvalidDataException>(() => NasPathProtocol.Parse(PathData.Layout, exitCode, PathData.Output()));
    }

    [Fact]
    public void FullOctalModeWithSpecialBitsIsAcceptedAndDecoded()
    {
        var output = PathData.Output(media: "entry=directory:0:same:41:1026:100:7777:empty");
        var media = PathData.Snapshot(output).Observation(RemotePathRole.MediaMovies);
        Assert.Equal(RemotePathObservation.MaximumMode, media.Mode);
        Assert.False((media with { Mode = RemotePathObservation.MaximumMode + 1 }).IsValid);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2042, 200)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void NumericSessionIdentityIsIndependentOfDirectoryOwners(int userId, int groupId)
    {
        var output = PathData.Output().Replace("identity=1026:100", $"identity={userId}:{groupId}", StringComparison.Ordinal);
        var snapshot = PathData.Snapshot(output);

        Assert.Equal(new RemoteUnixIdentity(userId, groupId), snapshot.Identity);
        Assert.Equal(1026, snapshot.Observation(RemotePathRole.DataRoot).OwnerUid);
        Assert.Equal(0, snapshot.Observation(RemotePathRole.ConfigurationRoot).OwnerUid);
    }

    [Theory]
    [InlineData(1027, 100)]
    [InlineData(1026, 101)]
    public void RenumberingTheSameSshAccountChangesTheEnvironmentFingerprint(int userId, int groupId)
    {
        var before = PathData.Snapshot(PathData.Output());
        var output = PathData.Output().Replace("identity=1026:100", $"identity={userId}:{groupId}", StringComparison.Ordinal);
        var after = PathData.Snapshot(output);

        Assert.Equal(before.PrincipalFingerprint, after.PrincipalFingerprint);
        Assert.NotEqual(before.EnvironmentFingerprint, after.EnvironmentFingerprint);
    }

    [Theory]
    [InlineData(-1, 100)]
    [InlineData(1026, -1)]
    public void SnapshotRejectsInvalidNumericIdentityEvenWhenConstructedWithoutTheParser(int userId, int groupId)
    {
        var facts = NasPathProtocol.Parse(PathData.Layout, 0, PathData.Output());
        var invalid = facts with { Identity = new(userId, groupId) };

        var error = Assert.Throws<ArgumentException>(() => new RemotePathSnapshot(PreflightData.Target, PathData.Layout,
            PreflightData.Pin, PreflightData.Profile, PreflightData.Now, invalid));
        Assert.Equal("paths.identity.invalid", error.Message);
    }

    [Fact]
    public void EnvironmentFingerprintChangesWithObservedStateOrLayout()
    {
        var baseline = PathData.Snapshot(PathData.Output()).EnvironmentFingerprint;
        Assert.Equal(baseline, PathData.Snapshot(PathData.Output()).EnvironmentFingerprint);
        var created = PathData.Output(configuration: "entry=directory:0:same:41:1026:100:755:empty");
        Assert.NotEqual(baseline, PathData.Snapshot(created).EnvironmentFingerprint);
        var otherLayout = new MediaPathLayout("/volume1/data", "/volume1/docker/other");
        var facts = NasPathProtocol.Parse(otherLayout, 0, PathData.Output());
        var moved = new RemotePathSnapshot(PreflightData.Target, otherLayout, PreflightData.Pin, PreflightData.Profile, PreflightData.Now, facts);
        Assert.NotEqual(baseline, moved.EnvironmentFingerprint);
        var otherUser = PreflightData.Target with { Username = "other-synthetic-user" };
        var asOtherUser = new RemotePathSnapshot(otherUser, PathData.Layout, PreflightData.Pin, PreflightData.Profile, PreflightData.Now,
            NasPathProtocol.Parse(PathData.Layout, 0, PathData.Output()));
        Assert.NotEqual(baseline, asOtherUser.EnvironmentFingerprint);
    }

    [Fact]
    public void SnapshotRequiresOneObservationPerLayoutEntryInOrder()
    {
        var facts = NasPathProtocol.Parse(PathData.Layout, 0, PathData.Output());
        var reversed = facts.Observations.Reverse().ToArray();
        Assert.Throws<ArgumentException>(() => Snapshot(facts with { Ownership = new(OwnershipMarkerState.Present) }));
        Assert.Throws<ArgumentException>(() => Snapshot(facts with { Observations = reversed }));
        Assert.Throws<ArgumentException>(() => Snapshot(facts with { Observations = facts.Observations.Skip(1).ToArray() }));
        Assert.Throws<ArgumentException>(() => Snapshot(facts with { RootDevice = -1 }));
        var otherEndpoint = new SshConnectionTarget("other.example.invalid", 22, "synthetic-user");
        Assert.Throws<ArgumentException>(() => new RemotePathSnapshot(otherEndpoint, PathData.Layout, PreflightData.Pin, PreflightData.Profile, PreflightData.Now, facts));

        static RemotePathSnapshot Snapshot(RemotePathFacts facts) => new(PreflightData.Target, PathData.Layout,
            PreflightData.Pin, PreflightData.Profile, PreflightData.Now, facts);
    }

    [Theory]
    [InlineData(false, SshProbeStatus.HostKeyApprovalRequired)]
    [InlineData(true, SshProbeStatus.HostKeyChanged)]
    public async Task MissingOrChangedKeyNeverSendsThePathRequest(bool changed, SshProbeStatus status)
    {
        var wire = new PathWire();
        var result = await Probe(wire).InspectAsync(PathData.Layout, changed ? PreflightData.OtherPin : null, Token);
        Assert.Equal(status, result.Status);
        Assert.Null(result.Snapshot);
        Assert.Equal(0, wire.ReadCount);
        Assert.True(wire.Disposed);
    }

    [Fact]
    public async Task SuccessfulInspectionIsBoundToTheApprovedKeyAndBuildProfile()
    {
        var wire = new PathWire();
        var result = await Probe(wire).InspectAsync(PathData.Layout, PreflightData.Pin, Token);
        Assert.Equal(SshProbeStatus.Ready, result.Status);
        Assert.Equal(PreflightData.Pin, result.Snapshot!.HostKey);
        Assert.Equal(PinnedRuntimeProfile.Fingerprint, result.Snapshot.ProfileFingerprint);
        Assert.Same(PathData.Layout, wire.Layout);
        Assert.Equal(1, wire.ReadCount);
        Assert.True(wire.Disposed);
    }

    [Theory]
    [InlineData("auth", SshProbeStatus.AuthenticationRequired)]
    [InlineData("transport", SshProbeStatus.TransportFailure)]
    [InlineData("response", SshProbeStatus.InvalidResponse)]
    public async Task FailedReadReturnsOnlyATypedFailure(string fault, SshProbeStatus status)
    {
        var wire = new PathWire
        {
            Read = _ => throw fault switch
            {
                "auth" => new SshAuthenticationException("synthetic-secret"),
                "transport" => new SshConnectionException("synthetic-secret"),
                _ => new InvalidDataException("synthetic-secret"),
            },
        };
        var result = await Probe(wire).InspectAsync(PathData.Layout, PreflightData.Pin, Token);
        Assert.Equal(status, result.Status);
        Assert.Null(result.Snapshot);
        Assert.DoesNotContain("synthetic-secret", result.ToString());
    }

    [Fact]
    public async Task BusyCancelledAndTimedOutInspectionsAreDistinct()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wire = new PathWire
        {
            Read = async token =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new(PathData.RootDevice, [], new(OwnershipMarkerState.Absent), new(1026, 100));
            },
        };
        var probe = Probe(wire);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var pending = probe.InspectAsync(PathData.Layout, PreflightData.Pin, caller.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3), Token);
            Assert.Equal(SshProbeStatus.Busy, (await probe.InspectAsync(PathData.Layout, PreflightData.Pin, Token)).Status);
            caller.Cancel();
            Assert.Equal(SshProbeStatus.Cancelled, (await pending.WaitAsync(TimeSpan.FromSeconds(3), Token)).Status);
        }
        finally
        {
            caller.Cancel();
            await pending.WaitAsync(TimeSpan.FromSeconds(3), Token);
        }
        var slow = new PathWire { Read = wire.Read };
        var timedOut = await Probe(slow, TimeSpan.FromMilliseconds(100)).InspectAsync(PathData.Layout, PreflightData.Pin, Token);
        Assert.Equal(SshProbeStatus.TimedOut, timedOut.Status);
    }

    private static NasPathProbe Probe(PathWire wire, TimeSpan? timeout = null) => new(
        PreflightData.Target, _ => wire, timeout ?? TimeSpan.FromSeconds(5), new FixedClock());

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => PreflightData.Now;
    }

    private sealed class PathWire : INasPathWire
    {
        internal bool Disposed { get; private set; }

        internal int ReadCount { get; private set; }

        internal MediaPathLayout? Layout { get; private set; }

        internal Func<CancellationToken, Task<RemotePathFacts>>? Read { get; init; }

        public Task ConnectAsync(Func<string, byte[], bool> acceptKey, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!acceptKey("ssh-ed25519", PreflightData.PublicKey))
                throw new SshConnectionException("synthetic-host-key-refused");
            return Task.CompletedTask;
        }

        public Task<RemotePathFacts> ReadPathsAsync(MediaPathLayout layout, CancellationToken token)
        {
            ReadCount++;
            Layout = layout;
            return Read?.Invoke(token) ?? Task.FromResult(NasPathProtocol.Parse(layout, 0, PathData.Output()));
        }

        public Task<MarkerWriteOutcome> WriteOwnershipMarkerAsync(OwnershipMarkerRequest request, Action sending, CancellationToken token) =>
            throw new InvalidOperationException("test.read-only-probe");

        public Task<(int? ExitCode, string Output)> RunLockAsync(string request, Action sending, CancellationToken token) =>
            throw new InvalidOperationException("test.read-only-probe");

        public Task<(int? ExitCode, string Output)> ReadConfigurationFileAsync(string request, CancellationToken token) =>
            throw new InvalidOperationException("test.read-only-probe");

        public Task<(int? ExitCode, string Output)> WriteConfigurationFileAsync(byte[] request, Action sending, CancellationToken token) =>
            throw new InvalidOperationException("test.read-only-probe");

        public void Dispose() => Disposed = true;
    }
}

internal static class PathData
{
    internal static readonly MediaPathLayout Layout = new("/volume1/data", "/volume1/docker/hh-homelab");

    /// <summary>Fresh layout: data root exists and is empty on device 41, everything else is missing.</summary>
    internal const long RootDevice = 2;

    internal const string Marker = "marker=absent";

    internal static string Output(string? media = null, string? configuration = null, string marker = Marker) => string.Join("\n",
        "hh-homelab-wizard:nas-paths:v3",
        "root-device=2",
        "identity=1026:100",
        "entry=directory:0:same:41:1026:100:755:empty",
        "entry=missing:1:same:41:1026:100:755:not-applicable",
        "entry=missing:2:same:41:1026:100:755:not-applicable",
        "entry=missing:2:same:41:1026:100:755:not-applicable",
        "entry=missing:1:same:41:1026:100:755:not-applicable",
        media ?? "entry=missing:2:same:41:1026:100:755:not-applicable",
        "entry=missing:2:same:41:1026:100:755:not-applicable",
        configuration ?? "entry=missing:2:same:42:0:0:755:not-applicable",
        marker,
        "end") + "\n";

    internal static RemotePathSnapshot Snapshot(string output) => new(PreflightData.Target, Layout, PreflightData.Pin,
        PreflightData.Profile, PreflightData.Now, NasPathProtocol.Parse(Layout, 0, output));
}
