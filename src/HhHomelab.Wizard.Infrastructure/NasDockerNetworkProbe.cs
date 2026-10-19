using HhHomelab.Wizard.Application;
using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.Infrastructure;

/// <summary>Reads exact-name network matches over fresh pinned SSH. No network mutation, elevation,
/// ambient Docker context or cached success. A successful read does not prove ownership.</summary>
public sealed class NasDockerNetworkProbe : INasDockerNetworkProbe
{
    private readonly SshConnectionTarget target;

    private readonly Func<TimeSpan, INasDockerNetworkWire> createWire;

    private readonly TimeProvider clock;

    private readonly TimeSpan timeout;

    private int running;

    public NasDockerNetworkProbe(SshConnectionTarget target, SshProbeCredentials credentials, TimeSpan? timeout = null)
        : this(target, budget => new SshNetProbeWire(target, credentials, budget),
            timeout ?? TimeSpan.FromSeconds(20), TimeProvider.System)
    {
        ArgumentNullException.ThrowIfNull(credentials);
    }

    internal NasDockerNetworkProbe(SshConnectionTarget target, Func<TimeSpan, INasDockerNetworkWire> createWire,
        TimeSpan timeout, TimeProvider clock)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(2))
            throw new ArgumentException("network.timeout.invalid");

        this.target = target;
        this.createWire = createWire;
        this.timeout = timeout;
        this.clock = clock;
    }

    public async Task<DockerNetworkResult> InspectAsync(string networkName, SshHostKey? approvedKey, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            return Failed(SshProbeStatus.Busy);

        try
        {
            if (!DockerNetworkSnapshot.IsValidName(networkName) || target is not { IsValid: true })
                return Failed(SshProbeStatus.InvalidInput);
            if (approvedKey is not null && !ValidPin(approvedKey))
                return Failed(SshProbeStatus.InvalidInput);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(timeout);
            return await Task.Run(() => InspectCoreAsync(networkName, approvedKey, token, deadline.Token), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref running, 0);
        }
    }

    private bool ValidPin(SshHostKey key) => key.IsValid && key.EndpointFingerprint == target.EndpointFingerprint;

    private async Task<DockerNetworkResult> InspectCoreAsync(string networkName, SshHostKey? approvedKey,
        CancellationToken caller, CancellationToken budget)
    {
        var trust = new SshHostKeyCheck(target.EndpointFingerprint, approvedKey);
        try
        {
            budget.ThrowIfCancellationRequested();
            using var wire = createWire(timeout);
            await wire.ConnectAsync(trust.Accept, budget).ConfigureAwait(false);
            if (!trust.Accepted)
                return DockerNetworkResult.Failed(trust.Failure ?? SshProbeResult.Failed(SshProbeStatus.InvalidResponse));

            var facts = await wire.ReadNetworkAsync(networkName, budget).ConfigureAwait(false);
            budget.ThrowIfCancellationRequested();
            if (trust.Failure is not null)
                return DockerNetworkResult.Failed(trust.Failure);

            var snapshot = new DockerNetworkSnapshot(target, networkName, approvedKey!, PinnedRuntimeProfile.Fingerprint, clock.GetUtcNow(), facts);
            return DockerNetworkResult.Observed(snapshot);
        }
        catch (Exception exception)
        {
            if (caller.IsCancellationRequested)
                return Failed(SshProbeStatus.Cancelled);
            if (budget.IsCancellationRequested)
                return Failed(SshProbeStatus.TimedOut);
            if (trust.Failure is not null)
                return DockerNetworkResult.Failed(trust.Failure);
            return Failed(SshConnectionProbe.Classify(exception, checkingSftp: false));
        }
    }

    private static DockerNetworkResult Failed(SshProbeStatus status) => DockerNetworkResult.Failed(SshProbeResult.Failed(status));
}

internal interface INasDockerNetworkWire : IDisposable
{
    Task ConnectAsync(Func<string, byte[], bool> acceptKey, CancellationToken token);

    Task<DockerNetworkFacts> ReadNetworkAsync(string networkName, CancellationToken token);
}
