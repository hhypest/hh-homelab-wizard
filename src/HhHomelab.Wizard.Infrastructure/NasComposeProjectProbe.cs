using System.Text;
using HhHomelab.Wizard.Application;
using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.Infrastructure;

internal static class NasComposeProjectProtocol
{
    internal const string RequestMarker = "hh-homelab-wizard:nas-compose-request:v1";

    internal const string ResponseMarker = "hh-homelab-wizard:nas-compose:v1";

    private static readonly Lazy<string> EmbeddedCommand = new(ReadCommand);

    internal static string Command => EmbeddedCommand.Value;

    private static string ReadCommand()
    {
        using var stream = typeof(NasComposeProjectProtocol).Assembly.GetManifestResourceStream("nas-compose-read.sh")
            ?? throw new InvalidDataException("compose.command.missing");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        return reader.ReadToEnd().TrimEnd('\n');
    }

    internal static string Request(ComposeProjectIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return RequestMarker + "\n"
            + "project=" + identity.ProjectName + "\n"
            + "network-key=" + identity.NetworkKey + "\n"
            + "network=" + identity.NetworkName + "\n"
            + "service=" + identity.ServiceName + "\n"
            + "container=" + identity.ContainerName + "\n"
            + "directory=" + identity.WorkingDirectory + "\n"
            + "end\n";
    }

    /// <summary>Any deviation from the framing, a record after Unavailable, an unknown state or a duplicate
    /// rejects the whole answer; a script failure is never a smaller complete answer.</summary>
    internal static ComposeProjectFacts Parse(int? exitCode, string output)
    {
        if (exitCode != 0 || output.Length > SshProbeOutput.MaximumBytes)
            throw Invalid();

        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var maximumLines = 2 * ComposeProjectFacts.MaximumMatches + 4;
        if (lines.Length is < 4 || lines.Length > maximumLines || lines[^1].Length != 0)
            throw Invalid();
        if (lines[0] != ResponseMarker || lines[^2] != "end")
            throw Invalid();

        var status = lines[^3] switch
        {
            "status=available" => PreflightFactStatus.Available,
            "status=unavailable" => PreflightFactStatus.Unavailable,
            _ => throw Invalid(),
        };
        var networks = new List<ComposeNetworkMatch>();
        var containers = new List<ComposeContainerMatch>();
        foreach (var line in lines[1..^3])
        {
            // The script prints every network record before the first container record.
            var networkRecord = line.StartsWith("network=", StringComparison.Ordinal);
            if (networkRecord && containers.Count == 0)
            {
                networks.Add(Network(line["network=".Length..]));
                continue;
            }
            if (line.StartsWith("container=", StringComparison.Ordinal))
            {
                containers.Add(Container(line["container=".Length..]));
                continue;
            }

            throw Invalid();
        }

        try
        {
            return new(status, networks, containers);
        }
        catch (ArgumentException)
        {
            throw Invalid();
        }
    }

    private static ComposeNetworkMatch Network(string value)
    {
        var fields = value.Split(':');
        if (fields.Length != 2)
            throw Invalid();

        return new(fields[0], Ownership(fields[1]));
    }

    private static ComposeContainerMatch Container(string value)
    {
        var fields = value.Split(':');
        if (fields.Length != 3)
            throw Invalid();

        var state = fields[2] switch
        {
            "created" => ComposeContainerState.Created,
            "running" => ComposeContainerState.Running,
            "paused" => ComposeContainerState.Paused,
            "restarting" => ComposeContainerState.Restarting,
            "removing" => ComposeContainerState.Removing,
            "exited" => ComposeContainerState.Exited,
            "dead" => ComposeContainerState.Dead,
            _ => throw Invalid(),
        };
        return new(fields[0], Ownership(fields[1]), state);
    }

    private static ComposeResourceOwnership Ownership(string value) => value switch
    {
        "own" => ComposeResourceOwnership.Own,
        "foreign" => ComposeResourceOwnership.Foreign,
        _ => throw Invalid(),
    };

    private static InvalidDataException Invalid() => new("compose.response.invalid");
}

internal interface INasComposeWire : IDisposable
{
    Task ConnectAsync(Func<string, byte[], bool> acceptKey, CancellationToken token);

    Task<ComposeProjectFacts> ReadComposeProjectAsync(ComposeProjectIdentity identity, CancellationToken token);
}

/// <summary>Reads the network and container a Compose project of this installation would own, over fresh
/// pinned SSH. Ownership comes only from Compose labels compared on the NAS; nothing is started, stopped,
/// removed, pulled or adopted by name, and no success is cached.</summary>
public sealed class NasComposeProjectProbe : INasComposeProjectProbe
{
    private readonly SshConnectionTarget target;

    private readonly Func<TimeSpan, INasComposeWire> createWire;

    private readonly TimeProvider clock;

    private readonly TimeSpan timeout;

    private int running;

    public NasComposeProjectProbe(SshConnectionTarget target, SshProbeCredentials credentials, TimeSpan? timeout = null)
        : this(target, budget => new SshNetProbeWire(target, credentials, budget),
            timeout ?? TimeSpan.FromSeconds(30), TimeProvider.System)
    {
        ArgumentNullException.ThrowIfNull(credentials);
    }

    internal NasComposeProjectProbe(SshConnectionTarget target, Func<TimeSpan, INasComposeWire> createWire,
        TimeSpan timeout, TimeProvider clock)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(2))
            throw new ArgumentException("compose.timeout.invalid");

        this.target = target;
        this.createWire = createWire;
        this.timeout = timeout;
        this.clock = clock;
    }

    public async Task<ComposeProjectResult> InspectAsync(ComposeProjectIdentity identity, SshHostKey approvedKey, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            return Failed(SshProbeStatus.Busy);

        try
        {
            if (identity is null || target is not { IsValid: true })
                return Failed(SshProbeStatus.InvalidInput);
            if (approvedKey is null || !ValidPin(approvedKey))
                return Failed(SshProbeStatus.InvalidInput);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(timeout);
            return await Task.Run(() => InspectCoreAsync(identity, approvedKey, token, deadline.Token), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref running, 0);
        }
    }

    private bool ValidPin(SshHostKey key) => key.IsValid && key.EndpointFingerprint == target.EndpointFingerprint;

    private async Task<ComposeProjectResult> InspectCoreAsync(ComposeProjectIdentity identity, SshHostKey approvedKey,
        CancellationToken caller, CancellationToken budget)
    {
        var trust = new SshHostKeyCheck(target.EndpointFingerprint, approvedKey);
        try
        {
            budget.ThrowIfCancellationRequested();
            using var wire = createWire(timeout);
            await wire.ConnectAsync(trust.Accept, budget).ConfigureAwait(false);
            if (!trust.Accepted)
                return ComposeProjectResult.Failed(trust.Failure ?? SshProbeResult.Failed(SshProbeStatus.InvalidResponse));

            var facts = await wire.ReadComposeProjectAsync(identity, budget).ConfigureAwait(false);
            budget.ThrowIfCancellationRequested();
            if (trust.Failure is not null)
                return ComposeProjectResult.Failed(trust.Failure);

            var snapshot = new ComposeProjectSnapshot(target, identity, approvedKey, PinnedRuntimeProfile.Fingerprint,
                clock.GetUtcNow(), facts);
            return ComposeProjectResult.Observed(snapshot);
        }
        catch (Exception exception)
        {
            if (caller.IsCancellationRequested)
                return Failed(SshProbeStatus.Cancelled);
            if (budget.IsCancellationRequested)
                return Failed(SshProbeStatus.TimedOut);
            if (trust.Failure is not null)
                return ComposeProjectResult.Failed(trust.Failure);
            if (exception is InvalidDataException)
                return Failed(SshProbeStatus.InvalidResponse);

            return Failed(SshConnectionProbe.Classify(exception, checkingSftp: false));
        }
    }

    private static ComposeProjectResult Failed(SshProbeStatus status) => ComposeProjectResult.Failed(SshProbeResult.Failed(status));
}
