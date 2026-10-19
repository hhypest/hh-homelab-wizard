using HhHomelab.Wizard.Application;
using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using Renci.SshNet.Common;
using Xunit;

namespace HhHomelab.Wizard.UnitTests;

public sealed class DockerNetworkTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string NetworkId => new('a', 64);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-option")]
    [InlineData("_name")]
    [InlineData("name.*")]
    [InlineData("name\nend")]
    [InlineData("$(id)")]
    public async Task InvalidNamesNeverEnterTheCommandOrStartAConnection(string? name)
    {
        var wire = new NetworkWire();
        var result = await Probe(wire).InspectAsync(name!, PreflightData.Pin, Token);

        Assert.Equal(SshProbeStatus.InvalidInput, result.Status);
        Assert.Equal(0, wire.Connects);
        Assert.Throws<ArgumentException>(() => NasDockerNetworkProtocol.Request(name!));
    }

    [Fact]
    public void RequestIsStdinDataAndTheCommandUsesTheLocalSocketAndExactTemplateComparison()
    {
        Assert.Equal(NasDockerNetworkProtocol.RequestMarker + "\nnetwork=media-test\nend\n",
            NasDockerNetworkProtocol.Request("media-test"));
        Assert.DoesNotContain("media-test", NasDockerNetworkProtocol.Command, StringComparison.Ordinal);
        Assert.Contains("--host unix:///var/run/docker.sock", NasDockerNetworkProtocol.Command, StringComparison.Ordinal);
        Assert.Contains("--filter \"name=$network\"", NasDockerNetworkProtocol.Command, StringComparison.Ordinal);
        Assert.Contains("{{if eq .Name \\\"$network\\\"}}", NasDockerNetworkProtocol.Command, StringComparison.Ordinal);
        Assert.False(DockerNetworkSnapshot.IsValidName(new string('a', 64)));
        Assert.True(DockerNetworkSnapshot.IsValidName(new string('a', 63)));
    }

    [Fact]
    public void EmptySuccessfulInventoryAndUnavailableInventoryAreDifferentFacts()
    {
        var absent = NasDockerNetworkProtocol.Parse(0, Output());
        var unavailable = NasDockerNetworkProtocol.Parse(0, Output(status: "unavailable"));

        Assert.Equal(PreflightFactStatus.Available, absent.Status);
        Assert.Empty(absent.NetworkIds);
        Assert.Equal(PreflightFactStatus.Unavailable, unavailable.Status);
        Assert.Empty(unavailable.NetworkIds);
        var substringOnly = Output().Replace("status=", "skip\nstatus=", StringComparison.Ordinal);
        Assert.Empty(NasDockerNetworkProtocol.Parse(0, substringOnly).NetworkIds);
    }

    [Fact]
    public void MatchesAreBoundedCopiedNormalizedAndIndependentOfObservationTime()
    {
        var source = new[] { new string('B', 64), NetworkId };
        var facts = new DockerNetworkFacts(PreflightFactStatus.Available, source);
        var before = Snapshot(facts);
        source[0] = new string('c', 64);
        var after = Snapshot(new(PreflightFactStatus.Available, [NetworkId, new string('b', 64)]), PreflightData.Now.AddSeconds(5));

        Assert.Equal([NetworkId, new string('b', 64)], facts.NetworkIds);
        Assert.Equal(before.EnvironmentFingerprint, after.EnvironmentFingerprint);
        Assert.Throws<ArgumentException>(() => new DockerNetworkFacts(PreflightFactStatus.Available,
            Enumerable.Range(0, DockerNetworkFacts.MaximumMatches + 1).Select(number => number.ToString("x64"))));
    }

    [Theory]
    [InlineData("bad-id")]
    [InlineData("partial-denial")]
    [InlineData("duplicate")]
    [InlineData("unknown-field")]
    [InlineData("unknown-status")]
    [InlineData("old-header")]
    [InlineData("missing-end")]
    [InlineData("oversized")]
    public void InvalidOrPartialResponsesCannotProveAbsence(string fault)
    {
        var output = fault switch
        {
            "bad-id" => Output(["not-an-id"]),
            "partial-denial" => Output([NetworkId], "unavailable"),
            "duplicate" => Output([NetworkId, NetworkId]),
            "unknown-field" => Output().Replace("status=", "unexpected=", StringComparison.Ordinal),
            "unknown-status" => Output(status: "missing"),
            "old-header" => Output().Replace(":v1", ":v0", StringComparison.Ordinal),
            "missing-end" => Output().Replace("end\n", "", StringComparison.Ordinal),
            _ => new string('x', 16385),
        };

        Assert.Equal("network.response.invalid", Assert.Throws<InvalidDataException>(() =>
            NasDockerNetworkProtocol.Parse(0, output)).Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public void NonzeroOrMissingCommandExitRejectsEvenACompleteSuccessResponse(int? exitCode)
    {
        Assert.Throws<InvalidDataException>(() => NasDockerNetworkProtocol.Parse(exitCode, Output()));
    }

    [Theory]
    [InlineData(false, SshProbeStatus.HostKeyApprovalRequired)]
    [InlineData(true, SshProbeStatus.HostKeyChanged)]
    public async Task TrustIsResolvedBeforeTheNetworkRequest(bool changed, SshProbeStatus expected)
    {
        var wire = new NetworkWire();
        var result = await Probe(wire).InspectAsync("media-test", changed ? PreflightData.OtherPin : null, Token);

        Assert.Equal(expected, result.Status);
        Assert.Equal(0, wire.Reads);
        Assert.True(wire.Disposed);
    }

    [Fact]
    public async Task ObservationIsBoundToTheSelectedNamePinPrincipalAndBuildProfile()
    {
        var wire = new NetworkWire { Facts = new(PreflightFactStatus.Available, [NetworkId]) };
        var result = await Probe(wire).InspectAsync("media-test", PreflightData.Pin, Token);
        var snapshot = Assert.IsType<DockerNetworkSnapshot>(result.Snapshot);

        Assert.Equal(SshProbeStatus.Ready, result.Status);
        Assert.Equal("media-test", wire.Name);
        Assert.True(snapshot.IsBoundTo(PreflightData.Target, "media-test", PreflightData.Pin, PinnedRuntimeProfile.Fingerprint));
        Assert.False(snapshot.IsBoundTo(PreflightData.Target, "other", PreflightData.Pin, PinnedRuntimeProfile.Fingerprint));
        Assert.False(snapshot.IsBoundTo(PreflightData.Target with { Username = "other" }, "media-test", PreflightData.Pin, PinnedRuntimeProfile.Fingerprint));
        Assert.Equal([NetworkId], snapshot.Facts.NetworkIds);
        Assert.True(wire.Disposed);
    }

    [Theory]
    [InlineData("auth", SshProbeStatus.AuthenticationRequired)]
    [InlineData("response", SshProbeStatus.InvalidResponse)]
    [InlineData("transport", SshProbeStatus.TransportFailure)]
    public async Task ReadFailuresHaveSafeTypedResults(string fault, SshProbeStatus expected)
    {
        var wire = new NetworkWire
        {
            Read = _ => throw fault switch
            {
                "auth" => new SshAuthenticationException("synthetic-secret"),
                "response" => new InvalidDataException("synthetic-secret"),
                _ => new SshConnectionException("synthetic-secret"),
            },
        };
        var result = await Probe(wire).InspectAsync("media-test", PreflightData.Pin, Token);

        Assert.Equal(expected, result.Status);
        Assert.Null(result.Snapshot);
        Assert.DoesNotContain("synthetic-secret", result.ToString());
    }

    [Fact]
    public async Task BusyCallerCancellationAndDeadlineDoNotReturnCachedSuccess()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wire = new NetworkWire
        {
            Read = async token =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new(PreflightFactStatus.Available, []);
            },
        };
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var probe = Probe(wire);
        var pending = probe.InspectAsync("media-test", PreflightData.Pin, caller.Token);
        try
        {
            await entered.Task.WaitAsync(Token);
            Assert.Equal(SshProbeStatus.Busy, (await probe.InspectAsync("media-test", PreflightData.Pin, Token)).Status);
            caller.Cancel();
            Assert.Equal(SshProbeStatus.Cancelled, (await pending.WaitAsync(Token)).Status);
        }
        finally
        {
            caller.Cancel();
            await pending.WaitAsync(Token);
        }
        var timed = await Probe(new NetworkWire { Read = wire.Read }, TimeSpan.FromMilliseconds(100))
            .InspectAsync("media-test", PreflightData.Pin, Token);
        Assert.Equal(SshProbeStatus.TimedOut, timed.Status);
        Assert.Null(timed.Snapshot);
    }

    [Theory]
    [InlineData("network-key", OperationErrorCode.IdentityMismatch)]
    [InlineData("network-principal", OperationErrorCode.IdentityMismatch)]
    [InlineData("network-profile", OperationErrorCode.IdentityMismatch)]
    [InlineData("network-name", OperationErrorCode.IdentityMismatch)]
    [InlineData("network-future", OperationErrorCode.ObservationChanged)]
    [InlineData("network-old", OperationErrorCode.ObservationChanged)]
    public async Task CompilerRejectsUnboundOrStaleNetworkEvidence(string change, OperationErrorCode expected)
    {
        var fixture = new QbittorrentPlanTests.PlanData { Change = change };
        var result = await fixture.Compiler.CompileAsync(fixture.Request(), PreflightData.Pin, Token);

        Assert.Null(result.Value);
        Assert.Equal(expected, result.Failure!.Code);
    }

    [Theory]
    [InlineData(true, "plan.network-name.conflict")]
    [InlineData(false, "plan.network-name.unavailable")]
    public async Task ExistingOrUnreadableNetworkBlocksAndInvalidatesTheOldPlan(bool available, string code)
    {
        var fixture = new QbittorrentPlanTests.PlanData();
        var before = (await fixture.Compiler.CompileAsync(fixture.Request(), PreflightData.Pin, Token)).Value!;
        Assert.Contains(before.Checks, check => check.Code == "plan.network-name" && check.Status == PreflightCheckStatus.Passed);
        fixture.Networks.Facts = available ? new(PreflightFactStatus.Available, [NetworkId]) : new(PreflightFactStatus.Unavailable, []);

        var renewed = await fixture.Compiler.RevalidateAsync(before, PreflightData.Pin, Token);

        Assert.False(renewed.Unchanged);
        Assert.Contains(renewed.Fresh.Value!.Checks, check => check.Code == code && check.Status == PreflightCheckStatus.Blocking);
        Assert.Equal(2, fixture.Networks.Calls);
    }

    [Fact]
    public async Task FailedNetworkRevalidationDoesNotReusePriorEvidence()
    {
        var fixture = new QbittorrentPlanTests.PlanData();
        var before = (await fixture.Compiler.CompileAsync(fixture.Request(), PreflightData.Pin, Token)).Value!;
        fixture.Networks.Status = SshProbeStatus.TransportFailure;

        var result = await fixture.Compiler.RevalidateAsync(before, PreflightData.Pin, Token);

        Assert.False(result.Unchanged);
        Assert.Null(result.Fresh.Value);
        Assert.Equal(OperationErrorCode.ReadFailed, result.Fresh.Failure!.Code);
    }

    private static string Output(string[]? ids = null, string status = "available") =>
        NasDockerNetworkProtocol.ResponseMarker + "\n" + string.Concat((ids ?? []).Select(id => "network=" + id + "\n"))
        + "status=" + status + "\nend\n";

    private static DockerNetworkSnapshot Snapshot(DockerNetworkFacts facts, DateTimeOffset? observed = null) =>
        new(PreflightData.Target, "media-test", PreflightData.Pin, PreflightData.Profile, observed ?? PreflightData.Now, facts);

    private static NasDockerNetworkProbe Probe(NetworkWire wire, TimeSpan? timeout = null) =>
        new(PreflightData.Target, _ => wire, timeout ?? TimeSpan.FromSeconds(5), new Clock());

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => PreflightData.Now;
    }

    private sealed class NetworkWire : INasDockerNetworkWire
    {
        internal bool Disposed { get; private set; }

        internal int Connects { get; private set; }

        internal int Reads { get; private set; }

        internal string? Name { get; private set; }

        internal DockerNetworkFacts Facts = new(PreflightFactStatus.Available, []);

        internal Func<CancellationToken, Task<DockerNetworkFacts>>? Read;

        public Task ConnectAsync(Func<string, byte[], bool> acceptKey, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Connects++;
            if (!acceptKey("ssh-ed25519", PreflightData.PublicKey))
                throw new SshConnectionException("synthetic-key-rejected");

            return Task.CompletedTask;
        }

        public Task<DockerNetworkFacts> ReadNetworkAsync(string networkName, CancellationToken token)
        {
            Reads++;
            Name = networkName;
            return Read?.Invoke(token) ?? Task.FromResult(Facts);
        }

        public void Dispose() => Disposed = true;
    }
}
