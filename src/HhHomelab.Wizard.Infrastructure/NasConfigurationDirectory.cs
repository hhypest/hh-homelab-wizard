using System.Text;
using HhHomelab.Wizard.Application;
using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.Infrastructure;

/// <summary>Result of one directory creation. Only Created/AlreadyPresent acknowledge the command; Rejected,
/// NotSent and IdentityChanged prove that nothing changed; Unknown means the NAS may have changed.</summary>
public enum DirectoryCreateOutcome
{
    Created,
    AlreadyPresent,
    Rejected,
    NotSent,
    IdentityChanged,
    Unknown,
}

public enum RemoteDirectoryState
{
    NotChecked,
    Absent,
    Present,
    NotDirectory,
    Redirected,
    Unavailable,
}

/// <summary>Read-only facts of one directory below the root; contents are never listed. Mode carries the full
/// bits including an inherited setgid; OwnedBySshUser tells this user's directory from, say, Docker's.</summary>
public sealed record RemoteDirectoryObservation(RemoteMarkerMatch Marker, RemoteDirectoryState Directory, int Mode = 0,
    bool OwnedBySshUser = false)
{
    public string Revision => ConfigurationRequest.Fingerprint(new { Marker, Directory, Mode, OwnedBySshUser });

    /// <summary>Only the permission bits are compared; special bits a parent share passes down do not count.</summary>
    public bool HasPermissions(int mode) => (Mode & 0x1FF) == mode;
}

public sealed record RemoteDirectoryRead(SshProbeStatus Status, RemoteDirectoryObservation? Observation)
{
    public static RemoteDirectoryRead Failed(SshProbeStatus status) => new(status, null);
}

/// <summary>Wire for the two fixed directory scripts; separate from the path wire so existing wires and
/// fakes are unaffected.</summary>
internal interface INasDirectoryWire : IDisposable
{
    Task ConnectAsync(Func<string, byte[], bool> acceptKey, CancellationToken token);

    Task<(int? ExitCode, string Output)> ReadConfigurationDirectoryAsync(string request, CancellationToken token);

    /// <summary>Calls <paramref name="sending"/> just before the request is written to the started command.</summary>
    Task<(int? ExitCode, string Output)> CreateConfigurationDirectoryAsync(string request, Action sending, CancellationToken token);
}

internal static class NasDirectoryProtocol
{
    internal const string ReadRequestMarker = "hh-homelab-wizard:nas-directory-read-request:v1";

    internal const string CreateRequestMarker = "hh-homelab-wizard:nas-directory-create-request:v1";

    private static readonly Lazy<string> EmbeddedRead = new(() => ReadCommand("nas-directory-read.sh"));

    private static readonly Lazy<string> EmbeddedCreate = new(() => ReadCommand("nas-directory-create.sh"));

    internal static string ReadCommandText => EmbeddedRead.Value;

    internal static string CreateCommandText => EmbeddedCreate.Value;

    private static string ReadCommand(string name)
    {
        using var stream = typeof(NasDirectoryProtocol).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidDataException("directory.command.missing");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        return reader.ReadToEnd().TrimEnd('\n');
    }

    internal static string ReadRequest(ConfigurationDirectoryRequest request) => ReadRequestMarker + "\n"
        + "root=" + request.Layout.ConfigurationRoot + "\n"
        + "marker=" + OwnershipMarkerJson.Serialize(request.Marker) + "\n"
        + "path=" + request.RelativePath + "\n"
        + "end\n";

    internal static string CreateRequest(ConfigurationDirectoryRequest request) => CreateRequestMarker + "\n"
        + "root=" + request.Layout.ConfigurationRoot + "\n"
        + "marker=" + OwnershipMarkerJson.Serialize(request.Marker) + "\n"
        + "path=" + request.RelativePath + "\n"
        + "mode=" + Convert.ToString(request.Mode, 8) + "\n"
        + "end\n";

    /// <summary>Exit 2 (framing) and 3 (precondition) end before any change; every other unexpected status
    /// or output may follow a partial change.</summary>
    internal static DirectoryCreateOutcome ParseCreate(int? exitCode, string output)
    {
        var text = output.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (exitCode is 2 or 3 && text.Length == 0)
            return DirectoryCreateOutcome.Rejected;
        if (exitCode != 0)
            return DirectoryCreateOutcome.Unknown;
        return text switch
        {
            "directory=created\n" => DirectoryCreateOutcome.Created,
            "directory=already-present\n" => DirectoryCreateOutcome.AlreadyPresent,
            _ => DirectoryCreateOutcome.Unknown,
        };
    }

    /// <summary>Any deviation from the exact four-line shape rejects the whole observation.</summary>
    internal static RemoteDirectoryObservation ParseRead(int? exitCode, string output)
    {
        if (exitCode != 0)
            throw Invalid();
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length != 5 || lines[0] != "hh-homelab-wizard:nas-directory:v1" || lines[3] != "end" || lines[4].Length != 0)
            throw Invalid();
        if (!lines[1].StartsWith("marker=", StringComparison.Ordinal) || !lines[2].StartsWith("directory=", StringComparison.Ordinal))
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
        var state = lines[2]["directory=".Length..];
        if (marker != RemoteMarkerMatch.Same)
            return state == "not-checked" ? new(marker, RemoteDirectoryState.NotChecked) : throw Invalid();
        return state switch
        {
            "absent" => new(marker, RemoteDirectoryState.Absent),
            "not-directory" => new(marker, RemoteDirectoryState.NotDirectory),
            "redirected" => new(marker, RemoteDirectoryState.Redirected),
            "unavailable" => new(marker, RemoteDirectoryState.Unavailable),
            _ => Present(marker, state),
        };
    }

    private static RemoteDirectoryObservation Present(RemoteMarkerMatch marker, string value)
    {
        var fields = value.Split(':');
        if (fields.Length != 3 || fields[0] != "present" || fields[2] is not ("own" or "other"))
            throw Invalid();
        var mode = fields[1];
        if (mode.Length is 0 or > 4 || !mode.All(character => character is >= '0' and <= '7'))
            throw Invalid();
        return new(marker, RemoteDirectoryState.Present, Convert.ToInt32(mode, 8), fields[2] == "own");
    }

    private static InvalidDataException Invalid() => new("directory.response.invalid");
}

/// <summary>Reads and creates configuration directories through fixed scripts, each call over a fresh pinned
/// SSH connection. Paths travel only on stdin; no sudo, chmod/chown of existing directories or deletion.</summary>
public sealed class NasConfigurationDirectoryClient
{
    private readonly SshConnectionTarget target;
    private readonly Func<TimeSpan, INasDirectoryWire> createWire;
    private readonly TimeSpan timeout;
    private int reading;
    private int writing;

    public NasConfigurationDirectoryClient(SshConnectionTarget target, SshProbeCredentials credentials, TimeSpan? timeout = null)
        : this(target, budget => new SshNetProbeWire(target, credentials, budget), timeout ?? TimeSpan.FromSeconds(20))
    {
        ArgumentNullException.ThrowIfNull(credentials);
    }

    internal NasConfigurationDirectoryClient(SshConnectionTarget target, Func<TimeSpan, INasDirectoryWire> createWire, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(2))
            throw new ArgumentException("directory.timeout.invalid");
        this.target = target;
        this.createWire = createWire;
        this.timeout = timeout;
    }

    public async Task<RemoteDirectoryRead> ReadAsync(ConfigurationDirectoryRequest request, SshHostKey approvedKey, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(approvedKey);
        if (Interlocked.CompareExchange(ref reading, 1, 0) != 0)
            return RemoteDirectoryRead.Failed(SshProbeStatus.Busy);
        try
        {
            if (!request.IsValid() || !ValidPin(approvedKey))
                return RemoteDirectoryRead.Failed(SshProbeStatus.InvalidInput);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(timeout);
            return await Task.Run(() => ReadCoreAsync(request, approvedKey, token, deadline.Token), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref reading, 0);
        }
    }

    public async Task<DirectoryCreateOutcome> CreateAsync(ConfigurationDirectoryRequest request, SshHostKey approvedKey, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(approvedKey);
        if (Interlocked.CompareExchange(ref writing, 1, 0) != 0)
            return DirectoryCreateOutcome.NotSent;
        try
        {
            if (!request.IsValid() || !ValidPin(approvedKey))
                return DirectoryCreateOutcome.NotSent;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(timeout);
            return await Task.Run(() => CreateCoreAsync(request, approvedKey, deadline.Token), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref writing, 0);
        }
    }

    private bool ValidPin(SshHostKey key) => key.IsValid && key.EndpointFingerprint == target.EndpointFingerprint;

    private async Task<RemoteDirectoryRead> ReadCoreAsync(ConfigurationDirectoryRequest request, SshHostKey approvedKey,
        CancellationToken caller, CancellationToken budget)
    {
        var trust = new SshHostKeyCheck(target.EndpointFingerprint, approvedKey);
        try
        {
            budget.ThrowIfCancellationRequested();
            using var wire = createWire(timeout);
            await wire.ConnectAsync(trust.Accept, budget).ConfigureAwait(false);
            if (!trust.Accepted)
                return RemoteDirectoryRead.Failed(trust.Failure?.Status ?? SshProbeStatus.InvalidResponse);
            var (exitCode, output) = await wire.ReadConfigurationDirectoryAsync(NasDirectoryProtocol.ReadRequest(request), budget).ConfigureAwait(false);
            budget.ThrowIfCancellationRequested();
            return new(SshProbeStatus.Ready, NasDirectoryProtocol.ParseRead(exitCode, output));
        }
        catch (Exception exception)
        {
            if (caller.IsCancellationRequested)
                return RemoteDirectoryRead.Failed(SshProbeStatus.Cancelled);
            if (budget.IsCancellationRequested)
                return RemoteDirectoryRead.Failed(SshProbeStatus.TimedOut);
            if (trust.Failure is not null)
                return RemoteDirectoryRead.Failed(trust.Failure.Status);
            if (exception is InvalidDataException)
                return RemoteDirectoryRead.Failed(SshProbeStatus.InvalidResponse);
            return RemoteDirectoryRead.Failed(SshConnectionProbe.Classify(exception, checkingSftp: false));
        }
    }

    private async Task<DirectoryCreateOutcome> CreateCoreAsync(ConfigurationDirectoryRequest request, SshHostKey approvedKey, CancellationToken budget)
    {
        var trust = new SshHostKeyCheck(target.EndpointFingerprint, approvedKey);
        var sent = false;
        try
        {
            budget.ThrowIfCancellationRequested();
            using var wire = createWire(timeout);
            await wire.ConnectAsync(trust.Accept, budget).ConfigureAwait(false);
            if (trust.Failure is not null)
                return DirectoryCreateOutcome.IdentityChanged;
            if (!trust.Accepted)
                return DirectoryCreateOutcome.NotSent;
            budget.ThrowIfCancellationRequested();
            var (exitCode, output) = await wire.CreateConfigurationDirectoryAsync(NasDirectoryProtocol.CreateRequest(request), () => sent = true, budget).ConfigureAwait(false);
            return NasDirectoryProtocol.ParseCreate(exitCode, output);
        }
        catch (Exception)
        {
            // Once the request started leaving the client, a timeout, cancellation or lost channel proves nothing.
            if (sent)
                return DirectoryCreateOutcome.Unknown;
            return trust.Failure is not null ? DirectoryCreateOutcome.IdentityChanged : DirectoryCreateOutcome.NotSent;
        }
    }
}

/// <summary>Creating one configuration directory as a journaled operation. Only this installation's exact
/// marker proves ownership; only a directory of the SSH user with exactly these permission bits satisfies the
/// request, so a directory Docker created as root is a conflict, not a success. Anything else on
/// the path is a conflict and is never changed; the create script re-checks every precondition and never
/// touches an existing directory, so an interrupted attempt may be completed under the same pending intent.</summary>
public sealed class NasConfigurationDirectoryOperation : IConfigurationOperation, IOperationOwnership, IRepeatableConditionalApply
{
    private readonly ConfigurationDirectoryRequest request;
    private readonly NasConfigurationDirectoryClient client;
    private readonly SshHostKey pin;

    public NasConfigurationDirectoryOperation(ConfigurationDirectoryRequest request, NasConfigurationDirectoryClient client, SshHostKey pin)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(pin);
        if (!request.IsValid())
            throw new ArgumentException("directory.request.invalid");
        if (request.Identity.Resource.EndpointFingerprint != pin.EndpointFingerprint)
            throw new ArgumentException("directory.endpoint.mismatch");
        if (request.Owner.ProfileFingerprint != PinnedRuntimeProfile.Fingerprint)
            throw new ArgumentException("directory.profile.mismatch");
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
        var outcome = await client.CreateAsync(request, pin, token).ConfigureAwait(false);
        return outcome switch
        {
            DirectoryCreateOutcome.Created or DirectoryCreateOutcome.AlreadyPresent => OperationWriteAttempt.Acknowledged(),
            DirectoryCreateOutcome.Rejected => Write(OperationOutcomeKind.Rejected, OperationErrorCode.WriteRejected),
            DirectoryCreateOutcome.IdentityChanged => Write(OperationOutcomeKind.Conflict, OperationErrorCode.IdentityMismatch),
            DirectoryCreateOutcome.NotSent when token.IsCancellationRequested => Write(OperationOutcomeKind.Cancelled, OperationErrorCode.Cancelled),
            DirectoryCreateOutcome.NotSent => Write(OperationOutcomeKind.Transport, OperationErrorCode.ReadFailed),
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

    private async Task<OperationRead<StateRead>> ReadAsync(CancellationToken token)
    {
        var read = await client.ReadAsync(request, pin, token).ConfigureAwait(false);
        if (read.Observation is not { } observation)
            return OperationRead<StateRead>.Failed(ReadFailure(read.Status));
        var state = observation switch
        {
            { Marker: not RemoteMarkerMatch.Same } => ObservedResourceState.Conflict,
            { Directory: RemoteDirectoryState.Absent } => ObservedResourceState.Missing,
            { Directory: RemoteDirectoryState.Present, OwnedBySshUser: true } when observation.HasPermissions(request.Mode) => ObservedResourceState.Satisfied,
            _ => ObservedResourceState.Conflict,
        };
        return OperationRead<StateRead>.Observed(new(state, observation.Revision));
    }

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
