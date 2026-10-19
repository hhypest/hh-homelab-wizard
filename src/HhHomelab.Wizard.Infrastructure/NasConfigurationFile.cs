using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HhHomelab.Wizard.Application;
using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.Infrastructure;

/// <summary>Result of one file delivery. Only Created/AlreadyPresent acknowledge the command; Rejected,
/// NotSent and IdentityChanged prove that the target and its directories did not change; Unknown means
/// the NAS may have changed and must be observed.</summary>
public enum FileDeliveryOutcome
{
    Created,
    AlreadyPresent,
    Rejected,
    NotSent,
    IdentityChanged,
    Unknown,
}

/// <summary>Whether the configuration root carries exactly this installation's marker.</summary>
public enum RemoteMarkerMatch
{
    Same,
    Different,
    Absent,
    Redirected,
    Unavailable,
}

public enum RemoteFileState
{
    NotChecked,
    Absent,
    Present,
    NotRegular,
    Redirected,
    Unavailable,
    Oversized,
}

/// <summary>Read-only facts of one file. Contents never travel; only size, permission bits and SHA-256.</summary>
public sealed record RemoteFileObservation(RemoteMarkerMatch Marker, RemoteFileState File, long Size = 0, int Mode = 0, string? Sha256 = null)
{
    public string Revision => ConfigurationRequest.Fingerprint(new { Marker, File, Size, Mode, Sha256 });
}

public sealed record RemoteFileRead(SshProbeStatus Status, RemoteFileObservation? Observation)
{
    public static RemoteFileRead Failed(SshProbeStatus status) => new(status, null);
}

internal static class NasFileProtocol
{
    internal const string ReadRequestMarker = "hh-homelab-wizard:nas-file-read-request:v1";

    internal const string WriteRequestMarker = "hh-homelab-wizard:nas-file-write-request:v1";

    private static readonly Lazy<string> EmbeddedRead = new(() => ReadCommand("nas-file-read.sh"));

    private static readonly Lazy<string> EmbeddedWrite = new(() => ReadCommand("nas-file-write.sh"));

    internal static string ReadCommandText => EmbeddedRead.Value;

    internal static string WriteCommandText => EmbeddedWrite.Value;

    private static string ReadCommand(string name)
    {
        using var stream = typeof(NasFileProtocol).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidDataException("file.command.missing");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        return reader.ReadToEnd().TrimEnd('\n');
    }

    internal static string ReadRequest(ConfigurationFileRequest request) => ReadRequestMarker + "\n"
        + "root=" + request.Layout.ConfigurationRoot + "\n"
        + "marker=" + OwnershipMarkerJson.Serialize(request.Marker) + "\n"
        + "path=" + request.RelativePath + "\n"
        + "end\n";

    /// <summary>Header lines, then exactly <see cref="ConfigurationFileRequest.Length"/> raw bytes.</summary>
    internal static byte[] WriteRequest(ConfigurationFileRequest request)
    {
        var header = WriteRequestMarker + "\n"
            + "root=" + request.Layout.ConfigurationRoot + "\n"
            + "marker=" + OwnershipMarkerJson.Serialize(request.Marker) + "\n"
            + "path=" + request.RelativePath + "\n"
            + "mode=" + Octal(request.Mode) + "\n"
            + "size=" + request.Length.ToString(CultureInfo.InvariantCulture) + "\n"
            + "sha256=" + request.Sha256 + "\n"
            + "end\n";
        var bytes = new UTF8Encoding(false, true).GetBytes(header);
        var payload = new byte[bytes.Length + request.Length];
        bytes.CopyTo(payload, 0);
        request.Content.CopyTo(payload.AsSpan(bytes.Length));
        return payload;
    }

    internal static string Octal(int mode) => Convert.ToString(mode, 8);

    /// <summary>Exit 2 (framing) and 3 (precondition) end before the target changes; every other
    /// unexpected status or output may follow a partial change.</summary>
    internal static FileDeliveryOutcome ParseWrite(int? exitCode, string output)
    {
        var text = output.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (exitCode is 2 or 3 && text.Length == 0)
            return FileDeliveryOutcome.Rejected;
        if (exitCode != 0)
            return FileDeliveryOutcome.Unknown;
        return text switch
        {
            "file=created\n" => FileDeliveryOutcome.Created,
            "file=already-present\n" => FileDeliveryOutcome.AlreadyPresent,
            _ => FileDeliveryOutcome.Unknown,
        };
    }

    /// <summary>Any deviation from the exact four-line shape rejects the whole observation.</summary>
    internal static RemoteFileObservation ParseRead(int? exitCode, string output)
    {
        if (exitCode != 0)
            throw Invalid();
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length != 5 || lines[0] != "hh-homelab-wizard:nas-file:v1" || lines[3] != "end" || lines[4].Length != 0)
            throw Invalid();
        if (!lines[1].StartsWith("marker=", StringComparison.Ordinal) || !lines[2].StartsWith("file=", StringComparison.Ordinal))
            throw Invalid();
        var marker = lines[1]["marker=".Length..] switch
        {
            "same" => RemoteMarkerMatch.Same,
            "different" => RemoteMarkerMatch.Different,
            "absent" => RemoteMarkerMatch.Absent,
            "redirected" => RemoteMarkerMatch.Redirected,
            "unavailable" => RemoteMarkerMatch.Unavailable,
            _ => throw Invalid(),
        };
        var file = lines[2]["file=".Length..];
        if (marker != RemoteMarkerMatch.Same)
            return file == "not-checked" ? new(marker, RemoteFileState.NotChecked) : throw Invalid();
        return file switch
        {
            "absent" => new(marker, RemoteFileState.Absent),
            "not-regular" => new(marker, RemoteFileState.NotRegular),
            "redirected" => new(marker, RemoteFileState.Redirected),
            "unavailable" => new(marker, RemoteFileState.Unavailable),
            "oversized" => new(marker, RemoteFileState.Oversized),
            _ => Present(marker, file),
        };
    }

    private static RemoteFileObservation Present(RemoteMarkerMatch marker, string value)
    {
        var fields = value.Split(':');
        if (fields.Length != 4 || fields[0] != "present")
            throw Invalid();
        if (!long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size > ConfigurationFileRequest.MaximumContentBytes)
            throw Invalid();
        if (fields[2].Length is 0 or > 4 || !fields[2].All(character => character is >= '0' and <= '7'))
            throw Invalid();
        var mode = Convert.ToInt32(fields[2], 8);
        if (mode > 0xFFF || fields[3].Length != 64 || !fields[3].All(char.IsAsciiHexDigitLower))
            throw Invalid();
        return new(marker, RemoteFileState.Present, size, mode, fields[3]);
    }

    private static InvalidDataException Invalid() => new("file.response.invalid");
}

/// <summary>Reads and creates one configuration file through fixed scripts, each call over a fresh pinned
/// SSH connection. Paths and contents travel only on stdin; no sudo, chmod/chown of existing files,
/// replacement or deletion.</summary>
public sealed class NasConfigurationFileClient
{
    private readonly SshConnectionTarget target;
    private readonly Func<TimeSpan, INasPathWire> createWire;
    private readonly TimeSpan timeout;
    private int reading;
    private int writing;

    public NasConfigurationFileClient(SshConnectionTarget target, SshProbeCredentials credentials, TimeSpan? timeout = null)
        : this(target, budget => new SshNetProbeWire(target, credentials, budget), timeout ?? TimeSpan.FromSeconds(20))
    {
        ArgumentNullException.ThrowIfNull(credentials);
    }

    internal NasConfigurationFileClient(SshConnectionTarget target, Func<TimeSpan, INasPathWire> createWire, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(2))
            throw new ArgumentException("file.timeout.invalid");
        this.target = target;
        this.createWire = createWire;
        this.timeout = timeout;
    }

    public async Task<RemoteFileRead> ReadAsync(ConfigurationFileRequest request, SshHostKey approvedKey, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(approvedKey);
        if (Interlocked.CompareExchange(ref reading, 1, 0) != 0)
            return RemoteFileRead.Failed(SshProbeStatus.Busy);
        try
        {
            if (!request.IsValid() || !ValidPin(approvedKey))
                return RemoteFileRead.Failed(SshProbeStatus.InvalidInput);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(timeout);
            return await Task.Run(() => ReadCoreAsync(request, approvedKey, token, deadline.Token), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref reading, 0);
        }
    }

    public async Task<FileDeliveryOutcome> WriteAsync(ConfigurationFileRequest request, SshHostKey approvedKey, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(approvedKey);
        if (Interlocked.CompareExchange(ref writing, 1, 0) != 0)
            return FileDeliveryOutcome.NotSent;
        try
        {
            if (!request.IsValid() || !ValidPin(approvedKey))
                return FileDeliveryOutcome.NotSent;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(timeout);
            return await Task.Run(() => WriteCoreAsync(request, approvedKey, deadline.Token), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref writing, 0);
        }
    }

    private bool ValidPin(SshHostKey key) => key.IsValid && key.EndpointFingerprint == target.EndpointFingerprint;

    private async Task<RemoteFileRead> ReadCoreAsync(ConfigurationFileRequest request, SshHostKey approvedKey,
        CancellationToken caller, CancellationToken budget)
    {
        var trust = new SshHostKeyCheck(target.EndpointFingerprint, approvedKey);
        try
        {
            budget.ThrowIfCancellationRequested();
            using var wire = createWire(timeout);
            await wire.ConnectAsync(trust.Accept, budget).ConfigureAwait(false);
            if (!trust.Accepted)
                return RemoteFileRead.Failed(trust.Failure?.Status ?? SshProbeStatus.InvalidResponse);
            var (exitCode, output) = await wire.ReadConfigurationFileAsync(NasFileProtocol.ReadRequest(request), budget).ConfigureAwait(false);
            budget.ThrowIfCancellationRequested();
            return new(SshProbeStatus.Ready, NasFileProtocol.ParseRead(exitCode, output));
        }
        catch (Exception exception)
        {
            if (caller.IsCancellationRequested)
                return RemoteFileRead.Failed(SshProbeStatus.Cancelled);
            if (budget.IsCancellationRequested)
                return RemoteFileRead.Failed(SshProbeStatus.TimedOut);
            if (trust.Failure is not null)
                return RemoteFileRead.Failed(trust.Failure.Status);
            if (exception is InvalidDataException)
                return RemoteFileRead.Failed(SshProbeStatus.InvalidResponse);
            return RemoteFileRead.Failed(SshConnectionProbe.Classify(exception, checkingSftp: false));
        }
    }

    private async Task<FileDeliveryOutcome> WriteCoreAsync(ConfigurationFileRequest request, SshHostKey approvedKey, CancellationToken budget)
    {
        var trust = new SshHostKeyCheck(target.EndpointFingerprint, approvedKey);
        var sent = false;
        byte[]? payload = null;
        try
        {
            budget.ThrowIfCancellationRequested();
            using var wire = createWire(timeout);
            await wire.ConnectAsync(trust.Accept, budget).ConfigureAwait(false);
            if (trust.Failure is not null)
                return FileDeliveryOutcome.IdentityChanged;
            if (!trust.Accepted)
                return FileDeliveryOutcome.NotSent;
            budget.ThrowIfCancellationRequested();
            payload = NasFileProtocol.WriteRequest(request);
            var (exitCode, output) = await wire.WriteConfigurationFileAsync(payload, () => sent = true, budget).ConfigureAwait(false);
            return NasFileProtocol.ParseWrite(exitCode, output);
        }
        catch (Exception)
        {
            // After the whole request left the client a timeout, cancellation or lost channel proves nothing.
            if (sent)
                return FileDeliveryOutcome.Unknown;
            return trust.Failure is not null ? FileDeliveryOutcome.IdentityChanged : FileDeliveryOutcome.NotSent;
        }
        finally
        {
            // The transient copy with the file bytes lives only for this send.
            if (payload is not null)
                CryptographicOperations.ZeroMemory(payload);
        }
    }
}

/// <summary>Creating one configuration file as a journaled operation. Observe, Verify and the ownership
/// check are fresh read-only inspections: only this installation's exact marker proves ownership, and
/// only the exact bytes and mode satisfy the request. Any other existing file is a conflict, never
/// replaced; the write re-checks every precondition and publishes without clobbering, so an interrupted
/// attempt may be completed under the same pending intent.</summary>
public sealed class NasConfigurationFileOperation : IConfigurationOperation, IOperationOwnership, IRepeatableConditionalApply
{
    private readonly ConfigurationFileRequest request;
    private readonly NasConfigurationFileClient client;
    private readonly SshHostKey pin;

    public NasConfigurationFileOperation(ConfigurationFileRequest request, NasConfigurationFileClient client, SshHostKey pin)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(pin);
        if (!request.IsValid())
            throw new ArgumentException("file.request.invalid");
        // The journaled resource must name the NAS that is actually written: the pinned SSH endpoint.
        if (request.Identity.Resource.EndpointFingerprint != pin.EndpointFingerprint)
            throw new ArgumentException("file.endpoint.mismatch");
        // Observations are bound to this build's profile; a request of another profile never runs here.
        if (request.Owner.ProfileFingerprint != PinnedRuntimeProfile.Fingerprint)
            throw new ArgumentException("file.profile.mismatch");
        this.request = request;
        this.client = client;
        this.pin = pin;
    }

    public OperationDescriptor Descriptor => request.Descriptor;

    public async Task<OperationRead<OperationObservation>> ObserveAsync(CancellationToken token)
    {
        var read = await ReadAsync(token).ConfigureAwait(false);
        if (read.Failure is not null)
            return OperationRead<OperationObservation>.Failed(read.Failure);
        var (state, revision) = read.Value!;
        return OperationRead<OperationObservation>.Observed(new(Descriptor, state, revision));
    }

    public async Task<OperationWriteAttempt> ApplyAsync(OperationObservation baseline, CancellationToken token)
    {
        if (!baseline.IsValidFor(Descriptor) || baseline.State != ObservedResourceState.Missing)
            return OperationWriteAttempt.Failed(OperationResult.Failed(OperationOutcomeKind.Invalid, OperationErrorCode.InvalidRequest));
        var outcome = await client.WriteAsync(request, pin, token).ConfigureAwait(false);
        return outcome switch
        {
            FileDeliveryOutcome.Created or FileDeliveryOutcome.AlreadyPresent => OperationWriteAttempt.Acknowledged(),
            FileDeliveryOutcome.Rejected => Write(OperationOutcomeKind.Rejected, OperationErrorCode.WriteRejected),
            FileDeliveryOutcome.IdentityChanged => Write(OperationOutcomeKind.Conflict, OperationErrorCode.IdentityMismatch),
            // The connection failed before the whole request left the client: nothing was changed.
            FileDeliveryOutcome.NotSent when token.IsCancellationRequested => Write(OperationOutcomeKind.Cancelled, OperationErrorCode.Cancelled),
            FileDeliveryOutcome.NotSent => Write(OperationOutcomeKind.Transport, OperationErrorCode.ReadFailed),
            _ => Write(OperationOutcomeKind.Unknown, OperationErrorCode.WriteUncertain),
        };
    }

    public async Task<OperationRead<OperationVerification>> VerifyAsync(CancellationToken token)
    {
        var read = await ReadAsync(token).ConfigureAwait(false);
        if (read.Failure is not null)
            return OperationRead<OperationVerification>.Failed(read.Failure);
        var (state, revision) = read.Value!;
        return OperationRead<OperationVerification>.Observed(new(Descriptor, state, revision));
    }

    public async Task<ResourceOwnership> CheckAsync(ResourceIdentity resource, CancellationToken token)
    {
        if (resource != Descriptor.Identity.Resource)
            return ResourceOwnership.Foreign;
        var read = await client.ReadAsync(request, pin, token).ConfigureAwait(false);
        if (read.Observation is not { } observation)
            return ResourceOwnership.Unproven;
        return observation.Marker switch
        {
            RemoteMarkerMatch.Same => ResourceOwnership.Owned,
            RemoteMarkerMatch.Unavailable => ResourceOwnership.Unproven,
            _ => ResourceOwnership.Foreign,
        };
    }

    /// <summary>Missing: own marker and no file. Satisfied: exactly these bytes and mode. Conflict: any other
    /// file, a symlink or non-regular entry on the way, or a root that is not this installation's.</summary>
    private async Task<OperationRead<StateRead>> ReadAsync(CancellationToken token)
    {
        var read = await client.ReadAsync(request, pin, token).ConfigureAwait(false);
        if (read.Observation is not { } observation)
            return OperationRead<StateRead>.Failed(ReadFailure(read.Status));
        var state = observation switch
        {
            { Marker: not RemoteMarkerMatch.Same } => ObservedResourceState.Conflict,
            { File: RemoteFileState.Absent } => ObservedResourceState.Missing,
            { File: RemoteFileState.Present } when Matches(observation) => ObservedResourceState.Satisfied,
            _ => ObservedResourceState.Conflict,
        };
        return OperationRead<StateRead>.Observed(new(state, observation.Revision));
    }

    private bool Matches(RemoteFileObservation observation) => observation.Size == request.Length
        && observation.Mode == request.Mode
        && observation.Sha256 == request.Sha256;

    private static OperationResult ReadFailure(SshProbeStatus status) => status switch
    {
        SshProbeStatus.AuthenticationRequired => OperationResult.Failed(OperationOutcomeKind.Transport, OperationErrorCode.AuthenticationRequired),
        SshProbeStatus.Cancelled => OperationResult.Failed(OperationOutcomeKind.Cancelled, OperationErrorCode.Cancelled),
        SshProbeStatus.InvalidResponse => OperationResult.Failed(OperationOutcomeKind.Transport, OperationErrorCode.ResponseInvalid),
        SshProbeStatus.HostKeyApprovalRequired or SshProbeStatus.HostKeyChanged
            => OperationResult.Failed(OperationOutcomeKind.Conflict, OperationErrorCode.IdentityMismatch),
        _ => OperationResult.Failed(OperationOutcomeKind.Transport, OperationErrorCode.ReadFailed),
    };

    private static OperationWriteAttempt Write(OperationOutcomeKind kind, OperationErrorCode code) =>
        OperationWriteAttempt.Failed(OperationResult.Failed(kind, code));

    private sealed record StateRead(ObservedResourceState State, string Revision);
}
