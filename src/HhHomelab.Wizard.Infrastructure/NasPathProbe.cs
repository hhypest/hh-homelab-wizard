using HhHomelab.Wizard.Application;
using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.Infrastructure;

/// <summary>Fixed read-only path inspection over a fresh pinned SSH connection. Paths are sent as
/// stdin data to an embedded script; no elevation, writes, chmod/chown or cached observations.</summary>
public sealed class NasPathProbe : INasPathProbe
{
    private readonly SshConnectionTarget target;
    private readonly Func<TimeSpan, INasPathWire> createWire;
    private readonly TimeProvider clock;
    private readonly TimeSpan timeout;
    private int running;

    public NasPathProbe(SshConnectionTarget target, SshProbeCredentials credentials, TimeSpan? timeout = null)
        : this(target, budget => new SshNetProbeWire(target, credentials, budget),
            timeout ?? TimeSpan.FromSeconds(20), TimeProvider.System)
    {
        ArgumentNullException.ThrowIfNull(credentials);
    }

    internal NasPathProbe(SshConnectionTarget target, Func<TimeSpan, INasPathWire> createWire,
        TimeSpan timeout, TimeProvider clock)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(2))
            throw new ArgumentException("paths.timeout.invalid");
        this.target = target;
        this.createWire = createWire;
        this.timeout = timeout;
        this.clock = clock;
    }

    public async Task<RemotePathResult> InspectAsync(MediaPathLayout layout, SshHostKey? approvedKey, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            return Failed(SshProbeStatus.Busy);
        try
        {
            if (layout is null || target is not { IsValid: true })
                return Failed(SshProbeStatus.InvalidInput);
            if (approvedKey is not null && !ValidPin(approvedKey))
                return Failed(SshProbeStatus.InvalidInput);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(timeout);
            return await Task.Run(() => InspectCoreAsync(layout, approvedKey, token, deadline.Token), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref running, 0);
        }
    }

    private bool ValidPin(SshHostKey key) => key.IsValid && key.EndpointFingerprint == target.EndpointFingerprint;

    private async Task<RemotePathResult> InspectCoreAsync(MediaPathLayout layout, SshHostKey? approvedKey,
        CancellationToken caller, CancellationToken budget)
    {
        var trust = new SshHostKeyCheck(target.EndpointFingerprint, approvedKey);
        try
        {
            budget.ThrowIfCancellationRequested();
            using var wire = createWire(timeout);
            await wire.ConnectAsync(trust.Accept, budget).ConfigureAwait(false);
            if (!trust.Accepted)
                return RemotePathResult.Failed(trust.Failure ?? SshProbeResult.Failed(SshProbeStatus.InvalidResponse));
            var facts = await wire.ReadPathsAsync(layout, budget).ConfigureAwait(false);
            budget.ThrowIfCancellationRequested();
            if (trust.Failure is not null)
                return RemotePathResult.Failed(trust.Failure);
            var snapshot = new RemotePathSnapshot(target, layout, approvedKey!, PinnedRuntimeProfile.Fingerprint, clock.GetUtcNow(), facts);
            return RemotePathResult.Observed(snapshot);
        }
        catch (Exception exception)
        {
            if (caller.IsCancellationRequested)
                return Failed(SshProbeStatus.Cancelled);
            if (budget.IsCancellationRequested)
                return Failed(SshProbeStatus.TimedOut);
            if (trust.Failure is not null)
                return RemotePathResult.Failed(trust.Failure);
            return Failed(SshConnectionProbe.Classify(exception, checkingSftp: false));
        }
    }

    private static RemotePathResult Failed(SshProbeStatus status) => RemotePathResult.Failed(SshProbeResult.Failed(status));
}

internal interface INasPathWire : IDisposable
{
    Task ConnectAsync(Func<string, byte[], bool> acceptKey, CancellationToken token);

    Task<RemotePathFacts> ReadPathsAsync(MediaPathLayout layout, CancellationToken token);

    /// <summary>Calls <paramref name="sending"/> once the complete request was delivered to the command.</summary>
    Task<MarkerWriteOutcome> WriteOwnershipMarkerAsync(OwnershipMarkerRequest request, Action sending, CancellationToken token);

    /// <summary>Runs the fixed lock script with a prepared stdin request; returns raw exit code and output.
    /// Calls <paramref name="sending"/> once the complete request was delivered to the command.</summary>
    Task<(int? ExitCode, string Output)> RunLockAsync(string request, Action sending, CancellationToken token);

    /// <summary>Runs the fixed read-only file script with a prepared stdin request.</summary>
    Task<(int? ExitCode, string Output)> ReadConfigurationFileAsync(string request, CancellationToken token);

    /// <summary>Runs the fixed file write script; the request ends with the raw file bytes. Calls
    /// <paramref name="sending"/> once the complete request was delivered to the command.</summary>
    Task<(int? ExitCode, string Output)> WriteConfigurationFileAsync(byte[] request, Action sending, CancellationToken token);
}
