using HhHomelab.Wizard.Application;
using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.Infrastructure;

/// <summary>Fixed read-only inventory over a fresh pinned SSH connection. Does not elevate, accept
/// shell/path parameters, retain credentials, reuse observations or authorize installation.</summary>
public sealed class NasPreflightProbe : INasPreflightProbe
{
    private readonly SshConnectionTarget target;
    private readonly Func<TimeSpan, INasPreflightWire> createWire;
    private readonly TimeProvider clock;
    private readonly TimeSpan timeout;
    private int running;

    public NasPreflightProbe(SshConnectionTarget target, SshProbeCredentials credentials, TimeSpan? timeout = null)
        : this(target, budget => new SshNetProbeWire(target, credentials, budget),
            timeout ?? TimeSpan.FromSeconds(20), TimeProvider.System)
    {
        ArgumentNullException.ThrowIfNull(credentials);
    }

    internal NasPreflightProbe(SshConnectionTarget target, Func<TimeSpan, INasPreflightWire> createWire,
        TimeSpan timeout, TimeProvider clock)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(2))
            throw new ArgumentException("preflight.timeout.invalid");
        this.target = target;
        this.createWire = createWire;
        this.timeout = timeout;
        this.clock = clock;
    }

    public async Task<NasPreflightResult> CollectAsync(SshHostKey? approvedKey, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            return Failed(SshProbeStatus.Busy);
        try
        {
            if (target is not { IsValid: true })
                return Failed(SshProbeStatus.InvalidInput);
            if (approvedKey is not null && !ValidPin(approvedKey))
                return Failed(SshProbeStatus.InvalidInput);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(timeout);
            return await Task.Run(() => CollectCoreAsync(approvedKey, token, deadline.Token), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref running, 0);
        }
    }

    private bool ValidPin(SshHostKey key) => key.IsValid && key.EndpointFingerprint == target.EndpointFingerprint;

    private async Task<NasPreflightResult> CollectCoreAsync(SshHostKey? approvedKey, CancellationToken caller, CancellationToken budget)
    {
        var trust = new SshHostKeyCheck(target.EndpointFingerprint, approvedKey);
        try
        {
            budget.ThrowIfCancellationRequested();
            var started = clock.GetUtcNow();
            using var wire = createWire(timeout);
            await wire.ConnectAsync(trust.Accept, budget).ConfigureAwait(false);
            if (!trust.Accepted)
                return NasPreflightResult.Failed(trust.Failure ?? SshProbeResult.Failed(SshProbeStatus.InvalidResponse));
            var facts = await wire.ReadPreflightAsync(budget).ConfigureAwait(false);
            budget.ThrowIfCancellationRequested();
            if (trust.Failure is not null)
                return NasPreflightResult.Failed(trust.Failure);
            var snapshot = new NasPreflightSnapshot(target, approvedKey!, PinnedRuntimeProfile.Fingerprint, started, clock.GetUtcNow(), facts);
            return NasPreflightResult.Observed(snapshot);
        }
        catch (Exception exception)
        {
            if (caller.IsCancellationRequested)
                return Failed(SshProbeStatus.Cancelled);
            if (budget.IsCancellationRequested)
                return Failed(SshProbeStatus.TimedOut);
            if (trust.Failure is not null)
                return NasPreflightResult.Failed(trust.Failure);
            return Failed(SshConnectionProbe.Classify(exception, checkingSftp: false));
        }
    }

    private static NasPreflightResult Failed(SshProbeStatus status) => NasPreflightResult.Failed(SshProbeResult.Failed(status));
}

internal interface INasPreflightWire : IDisposable
{
    Task ConnectAsync(Func<string, byte[], bool> acceptKey, CancellationToken token);

    Task<NasPreflightFacts> ReadPreflightAsync(CancellationToken token);
}
