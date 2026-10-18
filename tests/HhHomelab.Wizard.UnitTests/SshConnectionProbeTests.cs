using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Security.Cryptography;
using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using Renci.SshNet.Common;
using Xunit;

namespace HhHomelab.Wizard.UnitTests;

public sealed class SshConnectionProbeTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly SshConnectionTarget Target = new("nas.example.invalid", 22, "synthetic-user");

    private static readonly byte[] PublicKey = Encoding.UTF8.GetBytes("synthetic-public-key-not-a-credential");

    private static SshHostKey Pin => SshHostKey.FromPublicKey(Target.EndpointFingerprint, "ssh-ed25519", PublicKey);

    [Fact]
    public async Task FirstUseReturnsKeyEvidenceWithoutAuthenticatingOrExecutingCommands()
    {
        var wire = new ProbeWire();
        var result = await Probe(wire).ProbeAsync(null, Token);
        Assert.Equal(SshProbeStatus.HostKeyApprovalRequired, result.Status);
        Assert.Equal(Pin, result.HostKey);
        Assert.False(wire.Authenticated);
        Assert.Equal(new[] { "connect", "dispose" }, wire.Events);
    }

    [Fact]
    public async Task ApprovedKeyPermitsOnlyReadOnlyEvidenceAndRechecksSftpHostKey()
    {
        var wire = new ProbeWire();
        var result = await Probe(wire).ProbeAsync(Pin, Token);
        Assert.Equal(SshProbeStatus.Ready, result.Status);
        Assert.Equal(new SshHostCapabilities(1026, true), result.Capabilities);
        Assert.Equal(new[] { "connect", "identity", "sudo-list", "sftp", "dispose" }, wire.Events);
        Assert.True(wire.Authenticated);
        Assert.True(wire.SftpAuthenticated);
    }

    [Fact]
    public async Task ExplicitApprovalReconnectsWithAFreshTransportAfterDisposingTheUntrustedAttempt()
    {
        var first = new ProbeWire();
        var second = new ProbeWire();
        var wires = new Queue<ProbeWire>([first, second]);
        var probe = new SshConnectionProbe(Target, _ => wires.Dequeue(), TimeSpan.FromSeconds(2));
        var untrusted = await probe.ProbeAsync(null, Token);
        Assert.Equal(SshProbeStatus.HostKeyApprovalRequired, untrusted.Status);
        Assert.Equal("dispose", first.Events.Last());
        Assert.Equal(SshProbeStatus.Ready, (await probe.ProbeAsync(untrusted.HostKey, Token)).Status);
        Assert.False(first.Authenticated);
        Assert.True(second.Authenticated);
    }

    [Fact]
    public async Task ChangedHostKeyNeverReachesAuthentication()
    {
        var wire = new ProbeWire { Key = [1, 2, 3] };
        var result = await Probe(wire).ProbeAsync(Pin, Token);
        Assert.Equal(SshProbeStatus.HostKeyChanged, result.Status);
        Assert.NotEqual(Pin, result.HostKey);
        Assert.False(wire.Authenticated);
        Assert.DoesNotContain("identity", wire.Events);
    }

    [Fact]
    public async Task SftpConnectionCannotReuseApprovalForADifferentKey()
    {
        var wire = new ProbeWire { SftpKey = [1, 2, 3] };
        var result = await Probe(wire).ProbeAsync(Pin, Token);
        Assert.Equal(SshProbeStatus.HostKeyChanged, result.Status);
        Assert.True(wire.Authenticated);
        Assert.False(wire.SftpAuthenticated);
        Assert.Null(result.Capabilities);
    }

    [Fact]
    public async Task PinIsBoundToHostAndPortBeforeCreatingATransport()
    {
        var wire = new ProbeWire();
        var other = Pin with { EndpointFingerprint = new string('b', 64) };
        var result = await Probe(wire).ProbeAsync(other, Token);
        Assert.Equal(SshProbeStatus.InvalidInput, result.Status);
        Assert.Empty(wire.Events);
    }

    [Theory]
    [InlineData("http://nas.example.invalid")]
    [InlineData("user@nas")]
    [InlineData("nas;echo secret")]
    [InlineData(" nas")]
    [InlineData("")]
    public async Task InvalidHostNeverOpensATransport(string host)
    {
        var wire = new ProbeWire();
        var probe = new SshConnectionProbe(Target with { Host = host }, _ => wire, TimeSpan.FromSeconds(1));
        Assert.Equal(SshProbeStatus.InvalidInput, (await probe.ProbeAsync(null, Token)).Status);
        Assert.Empty(wire.Events);
    }

    [Fact]
    public void EndpointCanonicalizationDoesNotMixPortsOrUsernamesIntoTrust()
    {
        Assert.Equal(Target.EndpointFingerprint, (Target with { Host = "NAS.EXAMPLE.INVALID.", Username = "another-user" }).EndpointFingerprint);
        Assert.NotEqual(Target.EndpointFingerprint, (Target with { Port = 2222 }).EndpointFingerprint);
        Assert.Equal((Target with { Host = "::1" }).EndpointFingerprint, (Target with { Host = "0:0:0:0:0:0:0:1" }).EndpointFingerprint);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AuthenticationAndTransportFailuresContainOnlyTypedStatus(bool authentication)
    {
        var wire = new ProbeWire
        {
            Failure = authentication ? new SshAuthenticationException("synthetic-secret") : new IOException("synthetic-secret"),
        };
        var result = await Probe(wire).ProbeAsync(Pin, Token);
        Assert.Equal(authentication ? SshProbeStatus.AuthenticationRequired : SshProbeStatus.TransportFailure, result.Status);
        Assert.DoesNotContain("synthetic-secret", result.ToString());
        Assert.Null(result.Capabilities);
        Assert.Equal("dispose", wire.Events.Last());
    }

    [Fact]
    public async Task MissingSftpIsNotReadyEvenAfterSuccessfulSshAuthentication()
    {
        var wire = new ProbeWire { SftpFailure = new SshException("synthetic-secret") };
        var result = await Probe(wire).ProbeAsync(Pin, Token);
        Assert.Equal(SshProbeStatus.SftpUnavailable, result.Status);
        Assert.Null(result.Capabilities);
    }

    [Fact]
    public async Task ATransportThatSkipsHostKeyEvidenceCannotBeReady()
    {
        var wire = new ProbeWire { SkipHostKey = true };
        Assert.Equal(SshProbeStatus.InvalidResponse, (await Probe(wire).ProbeAsync(Pin, Token)).Status);
        Assert.DoesNotContain("identity", wire.Events);
    }

    [Fact]
    public async Task SftpNeedsItsOwnHostKeyEvidenceEvenWhenSshWasVerified()
    {
        var wire = new ProbeWire { SkipSftpHostKey = true };
        Assert.Equal(SshProbeStatus.InvalidResponse, (await Probe(wire).ProbeAsync(Pin, Token)).Status);
    }

    [Fact]
    public async Task SudoListingFailureIsReportedWithoutClaimingPrivilege()
    {
        var wire = new ProbeWire { CanListSudo = false };
        var result = await Probe(wire).ProbeAsync(Pin, Token);
        Assert.Equal(SshProbeStatus.Ready, result.Status);
        Assert.False(result.Capabilities!.CanListSudoWithoutPrompt);
    }

    [Fact]
    public async Task DeadlineAndCallerCancellationAreDifferentAndDisposeTheTransport()
    {
        var deadlineWire = new ProbeWire { WaitForCancellation = true };
        var timed = new SshConnectionProbe(Target, _ => deadlineWire, TimeSpan.FromMilliseconds(80));
        Assert.Equal(SshProbeStatus.TimedOut, (await timed.ProbeAsync(Pin, Token)).Status);
        Assert.Equal("dispose", deadlineWire.Events.Last());

        var cancelledWire = new ProbeWire { WaitForCancellation = true };
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var running = Probe(cancelledWire).ProbeAsync(Pin, caller.Token);
        await cancelledWire.Started.Task.WaitAsync(Token);
        caller.Cancel();
        Assert.Equal(SshProbeStatus.Cancelled, (await running).Status);
        Assert.Equal("dispose", cancelledWire.Events.Last());
    }

    [Fact]
    public async Task SecondConcurrentProbeDoesNotStartAnotherConnection()
    {
        var wire = new ProbeWire { WaitForCancellation = true };
        var probe = Probe(wire);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var running = probe.ProbeAsync(Pin, caller.Token);
        await wire.Started.Task.WaitAsync(Token);
        Assert.Equal(SshProbeStatus.Busy, (await probe.ProbeAsync(Pin, Token)).Status);
        caller.Cancel();
        await running;
        Assert.Single(wire.Events, item => item == "connect");
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("sudo-list")]
    [InlineData("sftp")]
    public async Task OneDeadlineCoversReadsAndSftpAfterAuthentication(string phase)
    {
        var wire = new ProbeWire { DelayAt = phase };
        var probe = new SshConnectionProbe(Target, _ => wire, TimeSpan.FromMilliseconds(80));
        Assert.Equal(SshProbeStatus.TimedOut, (await probe.ProbeAsync(Pin, Token)).Status);
        Assert.Equal("dispose", wire.Events.Last());
    }

    [Theory]
    [InlineData("synthetic-secret")]
    [InlineData("hh-homelab-wizard:ssh-probe:v1\nDarwin\n1\n")]
    [InlineData("hh-homelab-wizard:ssh-probe:v1\nLinux\n-1\n")]
    [InlineData("hh-homelab-wizard:ssh-probe:v1\nLinux\n0\nextra\n")]
    public void RemoteIdentityOutputIsStrictAndDoesNotEscapeInExceptions(string output)
    {
        var error = Assert.Throws<InvalidDataException>(() => SshNetProbeWire.ParseIdentity(0, output));
        Assert.DoesNotContain(output, error.Message);
    }

    [Fact]
    public void IdentityNeedsSuccessfulExitStatusAndAValidLinuxUid()
    {
        const string output = "hh-homelab-wizard:ssh-probe:v1\nLinux\n1026\n";
        Assert.Equal(1026, SshNetProbeWire.ParseIdentity(0, output).UserId);
        Assert.Throws<InvalidDataException>(() => SshNetProbeWire.ParseIdentity(null, output));
        Assert.Throws<InvalidDataException>(() => SshNetProbeWire.ParseIdentity(1, output));
    }

    [Fact]
    public async Task OversizedCommandOutputIsBoundedAndCancelsItsCommand()
    {
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(Token);
        using var stream = new MemoryStream(new byte[SshProbeOutput.MaximumBytes + 1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => SshProbeOutput.ReadAsync(stream, retain: true, abort));
        Assert.True(abort.IsCancellationRequested);
    }

    [Fact]
    public async Task DiscardedStderrIsBoundedWithoutDecodingOrReturningItsContents()
    {
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(Token);
        using var stream = new MemoryStream([255, 254, 253]);
        Assert.Equal("", await SshProbeOutput.ReadAsync(stream, retain: false, abort));
    }

    [Fact]
    public void CredentialsAreRedactedAndDisposedCredentialsCannotReconnect()
    {
        using var credentials = SshProbeCredentials.Password("synthetic-secret");
        Assert.DoesNotContain("synthetic-secret", credentials.ToString());
        credentials.Dispose();
        Assert.Throws<ObjectDisposedException>(() => credentials.CreateAuthentication(Target, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void PasswordAuthenticationDoesNotFallBackToOtherCredentialSourcesOrLegacyAlgorithms()
    {
        using var credentials = SshProbeCredentials.Password("synthetic-secret");
        using var authentication = credentials.CreateAuthentication(Target, TimeSpan.FromSeconds(1));
        Assert.Single(authentication.ConnectionInfo.AuthenticationMethods);
        Assert.DoesNotContain("ssh-rsa", authentication.ConnectionInfo.HostKeyAlgorithms.Keys);
        Assert.DoesNotContain("ssh-dss", authentication.ConnectionInfo.HostKeyAlgorithms.Keys);
        Assert.DoesNotContain(authentication.ConnectionInfo.Encryptions.Keys, name => name.EndsWith("-cbc", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrivateKeyAuthenticationUsesAnOwnedMemoryCopyAndOptionalPassphrase(bool encrypted)
    {
        using var rsa = RSA.Create(2048);
        var pem = encrypted
            ? rsa.ExportEncryptedPkcs8PrivateKeyPem("synthetic-passphrase", new(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 1000))
            : rsa.ExportPkcs8PrivateKeyPem();
        var input = Encoding.UTF8.GetBytes(pem);
        using var credentials = SshProbeCredentials.PrivateKey(input, encrypted ? "synthetic-passphrase" : null);
        CryptographicOperations.ZeroMemory(input);
        using var authentication = credentials.CreateAuthentication(Target, TimeSpan.FromSeconds(1));
        Assert.Equal("publickey", Assert.Single(authentication.ConnectionInfo.AuthenticationMethods).Name);
        Assert.DoesNotContain("PRIVATE KEY", credentials.ToString());
    }

    [Fact]
    public async Task MalformedPrivateKeyReturnsOnlyInvalidInput()
    {
        using var credentials = SshProbeCredentials.PrivateKey("synthetic-secret"u8);
        var probe = new SshConnectionProbe(Target, credentials);
        var result = await probe.ProbeAsync(null, Token);
        Assert.Equal(SshProbeStatus.InvalidInput, result.Status);
        Assert.DoesNotContain("synthetic-secret", result.ToString());
    }

    [Fact]
    public async Task RealSshNetHandshakeHasADeadlineAgainstAStalledLoopbackSocket()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var credentials = SshProbeCredentials.Password("synthetic-secret");
        var probe = new SshConnectionProbe(new("127.0.0.1", port, "synthetic-user"), credentials, TimeSpan.FromMilliseconds(200));
        var pending = probe.ProbeAsync(null, Token);
        using var peer = await listener.AcceptTcpClientAsync(Token);
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(SshProbeStatus.TimedOut, result.Status);
    }

    [Fact]
    public async Task CallerCanCancelARealStalledSshNetHandshake()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var credentials = SshProbeCredentials.Password("synthetic-secret");
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var probe = new SshConnectionProbe(new("127.0.0.1", port, "synthetic-user"), credentials);
        var pending = probe.ProbeAsync(null, caller.Token);
        using var peer = await listener.AcceptTcpClientAsync(Token);
        caller.Cancel();
        Assert.Equal(SshProbeStatus.Cancelled, (await pending.WaitAsync(TimeSpan.FromSeconds(5), Token)).Status);
    }

    private static SshConnectionProbe Probe(ProbeWire wire) => new(Target, _ => wire, TimeSpan.FromSeconds(2));

    private sealed class ProbeWire : ISshProbeWire
    {
        public List<string> Events { get; } = [];

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public byte[] Key { get; init; } = PublicKey;

        public byte[]? SftpKey { get; init; }

        public bool Authenticated { get; private set; }

        public bool SftpAuthenticated { get; private set; }

        public bool CanListSudo { get; init; } = true;

        public bool WaitForCancellation { get; init; }

        public bool SkipHostKey { get; init; }

        public bool SkipSftpHostKey { get; init; }

        public string? DelayAt { get; init; }

        public Exception? Failure { get; init; }

        public Exception? SftpFailure { get; init; }

        public async Task ConnectAsync(Func<string, byte[], bool> acceptKey, CancellationToken token)
        {
            Events.Add("connect");
            Started.SetResult();
            if (WaitForCancellation)
                await Task.Delay(Timeout.Infinite, token);
            if (SkipHostKey)
                return;
            if (!acceptKey("ssh-ed25519", Key))
                throw new SshConnectionException("synthetic-untrusted");
            if (Failure is not null)
                throw Failure;
            Authenticated = true;
        }

        public async Task<SshProbeIdentity> ReadIdentityAsync(CancellationToken token)
        {
            Events.Add("identity");
            if (DelayAt == "identity")
                await Task.Delay(Timeout.Infinite, token);
            return new SshProbeIdentity(1026);
        }

        public async Task<bool> CanListSudoAsync(CancellationToken token)
        {
            Events.Add("sudo-list");
            if (DelayAt == "sudo-list")
                await Task.Delay(Timeout.Infinite, token);
            return CanListSudo;
        }

        public async Task CheckSftpAsync(Func<string, byte[], bool> acceptKey, CancellationToken token)
        {
            Events.Add("sftp");
            if (DelayAt == "sftp")
                await Task.Delay(Timeout.Infinite, token);
            if (SftpFailure is not null)
                throw SftpFailure;
            if (SkipSftpHostKey)
                return;
            if (!acceptKey("ssh-ed25519", SftpKey ?? Key))
                throw new SshConnectionException("synthetic-untrusted");
            SftpAuthenticated = true;
        }

        public void Dispose() => Events.Add("dispose");
    }
}
