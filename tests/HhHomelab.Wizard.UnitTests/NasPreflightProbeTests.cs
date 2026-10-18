using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using Renci.SshNet.Common;
using Xunit;

namespace HhHomelab.Wizard.UnitTests;

public sealed class NasPreflightProbeTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, SshProbeStatus.HostKeyApprovalRequired)]
    [InlineData(true, SshProbeStatus.HostKeyChanged)]
    public async Task MissingApprovalOrChangedKeyNeverAuthenticatesOrReadsInventory(bool changed, SshProbeStatus status)
    {
        var wire = new PreflightWire();
        var result = await Probe(wire).CollectAsync(changed ? PreflightData.OtherPin : null, Token);
        Assert.Equal(status, result.Status);
        Assert.Equal(PreflightData.Pin, result.HostKey);
        Assert.Null(result.Snapshot);
        Assert.False(wire.Authenticated);
        Assert.Equal(0, wire.ReadCount);
        Assert.True(wire.Disposed);
    }

    [Fact]
    public async Task WrongEndpointPinIsRejectedBeforeCreatingAnyWire()
    {
        var wire = new PreflightWire();
        var pin = PreflightData.Pin with { EndpointFingerprint = new string('c', 64) };
        var result = await Probe(wire).CollectAsync(pin, Token);
        Assert.Equal(SshProbeStatus.InvalidInput, result.Status);
        Assert.False(wire.Connected);
        Assert.False(wire.Disposed);
    }

    [Fact]
    public async Task MissingHostKeyEvidenceNeverAllowsAReadEvenWhenConnectReturns()
    {
        var wire = new PreflightWire { SkipKey = true };
        var result = await Probe(wire).CollectAsync(PreflightData.Pin, Token);
        Assert.Equal(SshProbeStatus.InvalidResponse, result.Status);
        Assert.Equal(0, wire.ReadCount);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public async Task EverySuccessfulCallUsesANewConnectionAndImmutableProfileBoundObservation()
    {
        var first = new PreflightWire();
        var second = new PreflightWire { Facts = PreflightData.Facts([new string('e', 64)]) };
        var wires = new Queue<PreflightWire>([first, second]);
        var probe = new NasPreflightProbe(PreflightData.Target, _ => wires.Dequeue(), TimeSpan.FromSeconds(2), new FixedClock());
        var one = await probe.CollectAsync(PreflightData.Pin, Token);
        var two = await probe.CollectAsync(PreflightData.Pin, Token);
        Assert.Equal(SshProbeStatus.Ready, one.Status);
        Assert.Equal(SshProbeStatus.Ready, two.Status);
        Assert.Equal(PinnedRuntimeProfile.Fingerprint, one.Snapshot!.ProfileFingerprint);
        Assert.Equal(PreflightData.Pin, one.Snapshot.HostKey);
        Assert.NotSame(one.Snapshot, two.Snapshot);
        Assert.NotEqual(one.Snapshot.EnvironmentFingerprint, two.Snapshot!.EnvironmentFingerprint);
        Assert.True(first.Disposed);
        Assert.True(second.Disposed);
        Assert.Equal(1, first.ReadCount);
        Assert.Equal(1, second.ReadCount);
    }

    [Theory]
    [InlineData("auth", SshProbeStatus.AuthenticationRequired)]
    [InlineData("transport", SshProbeStatus.TransportFailure)]
    [InlineData("response", SshProbeStatus.InvalidResponse)]
    [InlineData("timeout", SshProbeStatus.TimedOut)]
    public async Task FailedReadReturnsOnlyTypedFailureAndNoPartialOrSecretData(string fault, SshProbeStatus status)
    {
        var wire = new PreflightWire
        {
            Read = _ => throw fault switch
            {
                "auth" => new SshAuthenticationException("synthetic-secret"),
                "transport" => new SshConnectionException("synthetic-secret"),
                "response" => new InvalidDataException("synthetic-secret"),
                _ => new SshOperationTimeoutException("synthetic-secret"),
            },
        };
        var result = await Probe(wire).CollectAsync(PreflightData.Pin, Token);
        Assert.Equal(status, result.Status);
        Assert.Null(result.Snapshot);
        Assert.DoesNotContain("synthetic-secret", result.ToString());
        Assert.True(wire.Disposed);
    }

    [Fact]
    public async Task CallerCancellationAndBusyAreDistinctAndDoNotLeaveACachedSnapshot()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wire = new PreflightWire
        {
            Read = async token =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return PreflightData.Facts();
            },
        };
        var probe = Probe(wire);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var pending = probe.CollectAsync(PreflightData.Pin, caller.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3), Token);
            Assert.Equal(SshProbeStatus.Busy, (await probe.CollectAsync(PreflightData.Pin, Token)).Status);
            caller.Cancel();
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(3), Token);
            Assert.Equal(SshProbeStatus.Cancelled, result.Status);
            Assert.Null(result.Snapshot);
        }
        finally
        {
            caller.Cancel();
            await pending.WaitAsync(TimeSpan.FromSeconds(3), Token);
        }
        Assert.True(wire.Disposed);
    }

    [Fact]
    public async Task WholeProbeDeadlineStopsAReadAndPrecancelledCallerDoesNotConnect()
    {
        var wire = new PreflightWire { Read = async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return PreflightData.Facts();
        }};
        var result = await Probe(wire, TimeSpan.FromMilliseconds(100)).CollectAsync(PreflightData.Pin, Token);
        Assert.Equal(SshProbeStatus.TimedOut, result.Status);
        Assert.Null(result.Snapshot);
        Assert.True(wire.Disposed);
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var fresh = new PreflightWire();
        Assert.Equal(SshProbeStatus.Cancelled, (await Probe(fresh).CollectAsync(PreflightData.Pin, caller.Token)).Status);
        Assert.False(fresh.Connected);
    }

    private static NasPreflightProbe Probe(PreflightWire wire, TimeSpan? timeout = null) => new(
        PreflightData.Target, _ => wire, timeout ?? TimeSpan.FromSeconds(5), new FixedClock());

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => PreflightData.Now;
    }

    private sealed class PreflightWire : INasPreflightWire
    {
        internal bool SkipKey { get; init; }

        internal bool Connected { get; private set; }

        internal bool Authenticated { get; private set; }

        internal bool Disposed { get; private set; }

        internal int ReadCount { get; private set; }

        internal NasPreflightFacts Facts { get; init; } = PreflightData.Facts();
        internal Func<CancellationToken, Task<NasPreflightFacts>>? Read { get; init; }

        public Task ConnectAsync(Func<string, byte[], bool> acceptKey, CancellationToken token)
        {
            Connected = true;
            token.ThrowIfCancellationRequested();
            if (SkipKey)
                return Task.CompletedTask;
            if (!acceptKey("ssh-ed25519", PreflightData.PublicKey))
                throw new SshConnectionException("synthetic-host-key-refused");
            Authenticated = true;
            return Task.CompletedTask;
        }

        public Task<NasPreflightFacts> ReadPreflightAsync(CancellationToken token)
        {
            ReadCount++;
            return Read?.Invoke(token) ?? Task.FromResult(Facts);
        }

        public void Dispose() => Disposed = true;
    }
}
