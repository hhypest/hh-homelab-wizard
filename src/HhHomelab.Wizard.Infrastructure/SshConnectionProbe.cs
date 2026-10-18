using HhHomelab.Wizard.Application;
using HhHomelab.Wizard.Domain;
using Renci.SshNet.Common;

namespace HhHomelab.Wizard.Infrastructure;

/// <summary>Memory-only read-only connection probe. Does not accept arbitrary shell commands or
/// automatically trust keys. Credentials remain caller-owned and are never persisted or logged.</summary>
public sealed class SshConnectionProbe : ISshConnectionProbe
{
    private readonly SshConnectionTarget target;

    private readonly TimeSpan timeout;

    private readonly Func<TimeSpan, ISshProbeWire> createWire;

    private int running;

    public SshConnectionProbe(SshConnectionTarget target, SshProbeCredentials credentials, TimeSpan? timeout = null)
        : this(target, budget => new SshNetProbeWire(target, credentials, budget), timeout ?? TimeSpan.FromSeconds(20))
    {
        ArgumentNullException.ThrowIfNull(credentials);
    }

    internal SshConnectionProbe(SshConnectionTarget target, Func<TimeSpan, ISshProbeWire> createWire, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(2))
            throw new ArgumentException("ssh.probe.invalid-timeout");
        this.target = target;
        this.createWire = createWire;
        this.timeout = timeout;
    }

    public async Task<SshProbeResult> ProbeAsync(SshHostKey? approvedKey, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            return SshProbeResult.Failed(SshProbeStatus.Busy);
        try
        {
            if (target is not { IsValid: true })
                return SshProbeResult.Failed(SshProbeStatus.InvalidInput);
            if (approvedKey is not null && !ValidPin(approvedKey))
                return SshProbeResult.Failed(SshProbeStatus.InvalidInput);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(timeout);
            // SSH.NET channel setup and cleanup contain synchronous work; keep it off the UI thread.
            return await Task.Run(() => RunAsync(approvedKey, token, deadline.Token), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref running, 0);
        }
    }

    private bool ValidPin(SshHostKey key) => key.IsValid && key.EndpointFingerprint == target.EndpointFingerprint;

    private async Task<SshProbeResult> RunAsync(SshHostKey? approvedKey, CancellationToken caller, CancellationToken budget)
    {
        var trust = new HostKeyCheck(target.EndpointFingerprint, approvedKey);
        var sftpTrust = new HostKeyCheck(target.EndpointFingerprint, approvedKey);
        var checkingSftp = false;
        try
        {
            budget.ThrowIfCancellationRequested();
            using var wire = createWire(timeout);
            await wire.ConnectAsync(trust.Accept, budget).ConfigureAwait(false);
            if (!trust.Accepted)
                return trust.Failure ?? SshProbeResult.Failed(SshProbeStatus.InvalidResponse);
            var identity = await wire.ReadIdentityAsync(budget).ConfigureAwait(false);
            if (identity.UserId < 0)
                return SshProbeResult.Failed(SshProbeStatus.InvalidResponse);
            var canListSudo = await wire.CanListSudoAsync(budget).ConfigureAwait(false);
            checkingSftp = true;
            await wire.CheckSftpAsync(sftpTrust.Accept, budget).ConfigureAwait(false);
            budget.ThrowIfCancellationRequested();
            var trustFailure = trust.Failure ?? sftpTrust.Failure;
            if (trustFailure is not null)
                return trustFailure;
            if (!sftpTrust.Accepted)
                return SshProbeResult.Failed(SshProbeStatus.InvalidResponse);
            return SshProbeResult.Ready(approvedKey!, new(identity.UserId, canListSudo));
        }
        catch (Exception exception)
        {
            if (caller.IsCancellationRequested)
                return SshProbeResult.Failed(SshProbeStatus.Cancelled);
            if (budget.IsCancellationRequested)
                return SshProbeResult.Failed(SshProbeStatus.TimedOut);
            var trustFailure = trust.Failure ?? sftpTrust.Failure;
            if (trustFailure is not null)
                return trustFailure;
            return SshProbeResult.Failed(Classify(exception, checkingSftp));
        }
    }

    private static SshProbeStatus Classify(Exception exception, bool checkingSftp) => exception switch
    {
        SshAuthenticationException => SshProbeStatus.AuthenticationRequired,
        SshOperationTimeoutException or TimeoutException => SshProbeStatus.TimedOut,
        SshConnectionException => SshProbeStatus.TransportFailure,
        InvalidDataException => SshProbeStatus.InvalidResponse,
        ArgumentException or ObjectDisposedException => SshProbeStatus.InvalidInput,
        SshException when checkingSftp => SshProbeStatus.SftpUnavailable,
        _ => SshProbeStatus.TransportFailure,
    };

    private sealed class HostKeyCheck(string endpointFingerprint, SshHostKey? approvedKey)
    {
        public bool Accepted { get; private set; }

        public SshProbeResult? Failure { get; private set; }

        public bool Accept(string algorithm, byte[] publicKey)
        {
            Accepted = false;
            if (Failure is not null || publicKey.Length is <= 0 or > 65536)
                return false;
            var observed = SshHostKey.FromPublicKey(endpointFingerprint, algorithm, publicKey);
            if (!observed.IsValid)
                return false;
            if (approvedKey != observed)
            {
                Failure = SshProbeResult.Failed(approvedKey is null
                    ? SshProbeStatus.HostKeyApprovalRequired
                    : SshProbeStatus.HostKeyChanged, observed);
                return false;
            }
            Accepted = true;
            return true;
        }
    }
}

internal sealed record SshProbeIdentity(int UserId);

internal interface ISshProbeWire : IDisposable
{
    Task ConnectAsync(Func<string, byte[], bool> acceptKey, CancellationToken token);

    Task<SshProbeIdentity> ReadIdentityAsync(CancellationToken token);

    Task<bool> CanListSudoAsync(CancellationToken token);

    Task CheckSftpAsync(Func<string, byte[], bool> acceptKey, CancellationToken token);
}
