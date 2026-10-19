using HhHomelab.Wizard.Application;
using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using Renci.SshNet.Common;
using Xunit;

namespace HhHomelab.Wizard.UnitTests;

public sealed class InstallationLockTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly Guid Session = Guid.Parse("11111111-1111-4111-8111-111111111111");

    private static readonly InstallationOwner Owner = new(Guid.Parse("4f9e2c1a-7b3d-4e5f-9a8b-1c2d3e4f5a6b"), PreflightData.Profile);

    [Theory]
    [InlineData(InstallationLockAction.Acquire, 0, "lock=acquired\n", InstallationLockStatus.Acquired)]
    [InlineData(InstallationLockAction.Acquire, 0, "lock=reacquired\n", InstallationLockStatus.Reacquired)]
    [InlineData(InstallationLockAction.Release, 0, "lock=released\n", InstallationLockStatus.Released)]
    [InlineData(InstallationLockAction.Release, 0, "lock=not-held\n", InstallationLockStatus.NotHeld)]
    [InlineData(InstallationLockAction.Acquire, 3, "lock=not-owned\n", InstallationLockStatus.NotOwned)]
    [InlineData(InstallationLockAction.Acquire, 3, "lock=invalid\n", InstallationLockStatus.Invalid)]
    [InlineData(InstallationLockAction.Acquire, 2, "", InstallationLockStatus.Invalid)]
    [InlineData(InstallationLockAction.Acquire, 0, "lock=released\n", InstallationLockStatus.Unknown)]
    [InlineData(InstallationLockAction.Release, 0, "lock=acquired\n", InstallationLockStatus.Unknown)]
    [InlineData(InstallationLockAction.Acquire, 0, "", InstallationLockStatus.Unknown)]
    [InlineData(InstallationLockAction.Acquire, 1, "lock=acquired\n", InstallationLockStatus.Unknown)]
    [InlineData(InstallationLockAction.Acquire, null, "", InstallationLockStatus.Unknown)]
    [InlineData(InstallationLockAction.Acquire, 0, "lock=acquired\nextra\n", InstallationLockStatus.Unknown)]
    [InlineData(InstallationLockAction.Acquire, 3, "lock=held:not-a-guid:5\n", InstallationLockStatus.Unknown)]
    [InlineData(InstallationLockAction.Acquire, 3, "lock=held:1:2:3\n", InstallationLockStatus.Unknown)]
    [InlineData(InstallationLockAction.Acquire, 3, "lock=whatever\n", InstallationLockStatus.Unknown)]
    public void OnlyExactSuccessOrRefusalLinesAreTrusted(InstallationLockAction action, int? exitCode, string output, InstallationLockStatus expected)
    {
        Assert.Equal(expected, NasLockProtocol.Parse(action, exitCode, output).Status);
    }

    [Fact]
    public void HeldLockReportsHolderAndAgeOrUnknownWithoutTrustingOtherContent()
    {
        var held = NasLockProtocol.Parse(InstallationLockAction.Acquire, 3, "lock=held:22222222-2222-4222-8222-222222222222:42\n");
        Assert.Equal(InstallationLockStatus.HeldByOther, held.Status);
        Assert.Equal(Guid.Parse("22222222-2222-4222-8222-222222222222"), held.Holder);
        Assert.Equal(42, held.AgeSeconds);
        var unknown = NasLockProtocol.Parse(InstallationLockAction.Acquire, 3, "lock=held:unknown:unknown\n");
        Assert.Equal(InstallationLockStatus.HeldByOther, unknown.Status);
        Assert.Null(unknown.Holder);
        Assert.Null(unknown.AgeSeconds);
        Assert.False(unknown.IsHeldBySession);
    }

    [Fact]
    public void RequestCarriesRootMarkerAndSessionOnlyAsStdinData()
    {
        var marker = new OwnershipMarker(1, Owner.InstallationId, Owner.ProfileFingerprint);
        var lines = NasLockProtocol.Request(InstallationLockAction.Release, PathData.Layout, marker, Session).TrimEnd('\n').Split('\n');
        Assert.Equal(new[]
        {
            NasLockProtocol.RequestMarker,
            "action=release",
            "root=" + PathData.Layout.ConfigurationRoot,
            "marker=" + OwnershipMarkerJson.Serialize(marker),
            "session=11111111-1111-4111-8111-111111111111",
            "end",
        }, lines);
        Assert.DoesNotContain(PathData.Layout.ConfigurationRoot, NasLockProtocol.Command);
    }

    [Fact]
    public async Task LostChannelAfterSendIsUnknownAndBeforeSendIsNotSent()
    {
        var afterSend = new LockWire { Fail = WireFailure.AfterSend };
        Assert.Equal(InstallationLockStatus.Unknown, (await Lock(afterSend).AcquireAsync(PathData.Layout, Owner, Session, PreflightData.Pin, Token)).Status);
        var beforeSend = new LockWire { Fail = WireFailure.Connect };
        Assert.Equal(InstallationLockStatus.NotSent, (await Lock(beforeSend).AcquireAsync(PathData.Layout, Owner, Session, PreflightData.Pin, Token)).Status);
        Assert.Empty(beforeSend.Requests);
        Assert.Equal(InstallationLockStatus.NotSent, (await Lock(new LockWire()).AcquireAsync(PathData.Layout, Owner, Guid.Empty, PreflightData.Pin, Token)).Status);
        Assert.Equal(InstallationLockStatus.NotSent, (await Lock(new LockWire()).AcquireAsync(PathData.Layout, Owner, Session, PreflightData.OtherPin, Token)).Status);
    }

    [Fact]
    public async Task HeldLockStopsTheScenarioBeforeAnyStepAndReleasesTheLocalLease()
    {
        var wire = new LockWire { Acquire = "lock=held:22222222-2222-4222-8222-222222222222:5\n", AcquireExit = 3 };
        var operation = new OperationFixture();
        var locked = Journal(operation.Journal, wire);
        var result = await new ConfigurationOperationScenario(locked, operation).ExecuteAsync(operation, Token);
        Assert.Equal(OperationOutcomeKind.Conflict, result.Kind);
        Assert.Equal(OperationErrorCode.OperationInProgress, result.Code);
        Assert.Equal(new[] { "lock", "unlock" }, operation.Events);
        Assert.Equal(InstallationLockStatus.HeldByOther, locked.LastAcquire!.Status);
        Assert.Null(locked.LastRelease);
        Assert.Single(wire.Requests);
    }

    [Fact]
    public async Task HeldLockSpansTheWholeOperationAndIsReleasedAfterwards()
    {
        var wire = new LockWire();
        var operation = new OperationFixture();
        var locked = Journal(operation.Journal, wire);
        var result = await new ConfigurationOperationScenario(locked, operation).ExecuteAsync(operation, Token);
        Assert.True(result.IsSuccess);
        Assert.Equal(1, operation.ApplyCount);
        Assert.Equal("lock", operation.Events[0]);
        Assert.Equal("unlock", operation.Events[^1]);
        Assert.Equal(InstallationLockStatus.Acquired, locked.LastAcquire!.Status);
        Assert.Equal(InstallationLockStatus.Released, locked.LastRelease!.Status);
        Assert.StartsWith(NasLockProtocol.RequestMarker + "\naction=acquire", wire.Requests[0], StringComparison.Ordinal);
        Assert.StartsWith(NasLockProtocol.RequestMarker + "\naction=release", wire.Requests[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownAcquireIsALeaseConflictNotAHeldLock()
    {
        var wire = new LockWire { Fail = WireFailure.AfterSend };
        var events = new List<string>();
        var locked = Journal(new TestOperationJournal(events), wire);
        await Assert.ThrowsAsync<OperationLeaseUnavailableException>(() =>
            locked.AcquireAsync(OperationFixture.NewRequest("qbittorrent").Descriptor, Token));
        Assert.Equal(InstallationLockStatus.Unknown, locked.LastAcquire!.Status);
        Assert.Equal(new[] { "lock", "unlock" }, events);
    }

    [Fact]
    public async Task CallerCancellationBeforeTheLockRequestIsDeliveredIsCancelledNotAConflict()
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var wire = new LockWire { OnConnect = caller.Cancel };
        var operation = new OperationFixture();
        var locked = Journal(operation.Journal, wire);
        var result = await new ConfigurationOperationScenario(locked, operation).ExecuteAsync(operation, caller.Token);
        Assert.Equal(OperationOutcomeKind.Cancelled, result.Kind);
        Assert.Equal(InstallationLockStatus.NotSent, locked.LastAcquire!.Status);
        Assert.Empty(wire.Requests);
        Assert.Equal(new[] { "lock", "unlock" }, operation.Events);
    }

    private static NasInstallationLock Lock(LockWire wire) => new(PreflightData.Target, _ => wire, TimeSpan.FromSeconds(5));

    private static RemoteLockedOperationJournal Journal(IOperationJournal inner, LockWire wire) =>
        new(inner, Lock(wire), PathData.Layout, Owner, Session, PreflightData.Pin);

    private enum WireFailure
    {
        None,
        Connect,
        AfterSend,
    }

    private sealed class LockWire : INasPathWire
    {
        public List<string> Requests { get; } = [];

        public WireFailure Fail { get; init; }

        public string Acquire { get; init; } = "lock=acquired\n";

        public int AcquireExit { get; init; }

        public Action? OnConnect { get; init; }

        public Task ConnectAsync(Func<string, byte[], bool> acceptKey, CancellationToken token)
        {
            OnConnect?.Invoke();
            token.ThrowIfCancellationRequested();
            if (Fail == WireFailure.Connect || !acceptKey("ssh-ed25519", PreflightData.PublicKey))
                throw new SshConnectionException("synthetic-connect-failure");
            return Task.CompletedTask;
        }

        public Task<RemotePathFacts> ReadPathsAsync(MediaPathLayout layout, CancellationToken token) =>
            throw new InvalidOperationException("test.lock-only");

        public Task<MarkerWriteOutcome> WriteOwnershipMarkerAsync(OwnershipMarkerRequest request, Action sending, CancellationToken token) =>
            throw new InvalidOperationException("test.lock-only");

        public Task<(int? ExitCode, string Output)> RunLockAsync(string request, Action sending, CancellationToken token)
        {
            sending();
            Requests.Add(request);
            if (Fail == WireFailure.AfterSend)
                throw new SshConnectionException("synthetic-lost-channel");
            var release = request.Contains("\naction=release\n", StringComparison.Ordinal);
            return Task.FromResult<(int?, string)>(release ? (0, "lock=released\n") : (AcquireExit, Acquire));
        }

        public void Dispose()
        {
        }
    }
}
