using System.Text;
using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.Infrastructure;

/// <summary>Result of one marker write. Only Created/AlreadyPresent acknowledge the command; Rejected
/// and NotSent prove that nothing changed; Unknown means the NAS may have changed and must be observed.</summary>
public enum MarkerWriteOutcome
{
    Created,
    AlreadyPresent,
    Rejected,
    NotSent,
    Unknown,
}

internal static class NasMarkerWriteProtocol
{
    internal const string RequestMarker = "hh-homelab-wizard:nas-marker-request:v1";

    private static readonly Lazy<string> EmbeddedCommand = new(ReadCommand);

    internal static string Command => EmbeddedCommand.Value;

    private static string ReadCommand()
    {
        using var stream = typeof(NasMarkerWriteProtocol).Assembly.GetManifestResourceStream("nas-marker-write.sh")
            ?? throw new InvalidDataException("ownership.command.missing");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        return reader.ReadToEnd().TrimEnd('\n');
    }

    internal static string Request(OwnershipMarkerRequest request) => RequestMarker + "\n"
        + "root=" + request.Layout.ConfigurationRoot + "\n"
        + "marker=" + OwnershipMarkerJson.Serialize(request.Marker) + "\n"
        + "end\n";

    /// <summary>Exit 2 (framing) and 3 (precondition) end before the first change; every other
    /// unexpected status or output may follow a partial change.</summary>
    internal static MarkerWriteOutcome Parse(int? exitCode, string output)
    {
        var text = output.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (exitCode is 2 or 3 && text.Length == 0)
            return MarkerWriteOutcome.Rejected;
        if (exitCode != 0)
            return MarkerWriteOutcome.Unknown;
        return text switch
        {
            "marker=created\n" => MarkerWriteOutcome.Created,
            "marker=already-present\n" => MarkerWriteOutcome.AlreadyPresent,
            _ => MarkerWriteOutcome.Unknown,
        };
    }
}

/// <summary>Writes the ownership marker through a fixed script over a fresh pinned SSH connection. The
/// root and marker travel on stdin; no sudo, chmod/chown or replacement of an existing marker.</summary>
public sealed class NasOwnershipMarkerWriter
{
    private readonly SshConnectionTarget target;
    private readonly Func<TimeSpan, INasPathWire> createWire;
    private readonly TimeSpan timeout;
    private int running;

    public NasOwnershipMarkerWriter(SshConnectionTarget target, SshProbeCredentials credentials, TimeSpan? timeout = null)
        : this(target, budget => new SshNetProbeWire(target, credentials, budget), timeout ?? TimeSpan.FromSeconds(20))
    {
        ArgumentNullException.ThrowIfNull(credentials);
    }

    internal NasOwnershipMarkerWriter(SshConnectionTarget target, Func<TimeSpan, INasPathWire> createWire, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(2))
            throw new ArgumentException("ownership.timeout.invalid");
        this.target = target;
        this.createWire = createWire;
        this.timeout = timeout;
    }

    public async Task<MarkerWriteOutcome> WriteAsync(OwnershipMarkerRequest request, SshHostKey approvedKey, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(approvedKey);
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            return MarkerWriteOutcome.NotSent;
        try
        {
            var pinValid = approvedKey.IsValid && approvedKey.EndpointFingerprint == target.EndpointFingerprint;
            if (!request.IsValid() || !pinValid)
                return MarkerWriteOutcome.NotSent;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(timeout);
            return await Task.Run(() => WriteCoreAsync(request, approvedKey, deadline.Token), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref running, 0);
        }
    }

    private async Task<MarkerWriteOutcome> WriteCoreAsync(OwnershipMarkerRequest request, SshHostKey approvedKey, CancellationToken budget)
    {
        var trust = new SshHostKeyCheck(target.EndpointFingerprint, approvedKey);
        var sent = false;
        try
        {
            budget.ThrowIfCancellationRequested();
            using var wire = createWire(timeout);
            await wire.ConnectAsync(trust.Accept, budget).ConfigureAwait(false);
            if (!trust.Accepted || trust.Failure is not null)
                return MarkerWriteOutcome.NotSent;
            budget.ThrowIfCancellationRequested();
            return await wire.WriteOwnershipMarkerAsync(request, () => sent = true, budget).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // After the command left the client a timeout, cancellation or lost channel proves nothing.
            return sent ? MarkerWriteOutcome.Unknown : MarkerWriteOutcome.NotSent;
        }
    }
}
