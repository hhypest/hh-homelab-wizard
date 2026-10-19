using System.Globalization;
using System.Text;
using HhHomelab.Wizard.Application;
using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.Infrastructure;

public enum InstallationLockStatus
{
    Acquired,
    Reacquired,
    Released,
    NotHeld,
    HeldByOther,
    NotOwned,
    Invalid,
    NotSent,
    Unknown,
}

/// <summary>Holder is the other session's GUID when it was readable; age is seconds since the lock
/// directory was created, by the NAS clock. Neither is a reason to take the lock over automatically.</summary>
public sealed record InstallationLockResult(InstallationLockStatus Status, Guid? Holder = null, long? AgeSeconds = null)
{
    public bool IsHeldBySession => Status is InstallationLockStatus.Acquired or InstallationLockStatus.Reacquired;
}

public enum InstallationLockAction
{
    Acquire,
    Release,
}

internal static class NasLockProtocol
{
    internal const string RequestMarker = "hh-homelab-wizard:nas-lock-request:v1";

    private static readonly Lazy<string> EmbeddedCommand = new(ReadCommand);

    internal static string Command => EmbeddedCommand.Value;

    private static string ReadCommand()
    {
        using var stream = typeof(NasLockProtocol).Assembly.GetManifestResourceStream("nas-lock.sh")
            ?? throw new InvalidDataException("lock.command.missing");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        return reader.ReadToEnd().TrimEnd('\n');
    }

    internal static string Request(InstallationLockAction action, MediaPathLayout layout, OwnershipMarker marker, Guid session) =>
        RequestMarker + "\n"
        + "action=" + (action == InstallationLockAction.Acquire ? "acquire" : "release") + "\n"
        + "root=" + layout.ConfigurationRoot + "\n"
        + "marker=" + OwnershipMarkerJson.Serialize(marker) + "\n"
        + "session=" + session.ToString("D") + "\n"
        + "end\n";

    /// <summary>Exit 3 always reports a refusal made before any change; exit 0 a success. A missing,
    /// extra or malformed line, or any other status, may follow a partial change and is Unknown.</summary>
    internal static InstallationLockResult Parse(InstallationLockAction action, int? exitCode, string output)
    {
        var text = output.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (exitCode == 2 && text.Length == 0)
            return new(InstallationLockStatus.Invalid);
        if (!text.StartsWith("lock=", StringComparison.Ordinal) || !text.EndsWith('\n') || text.Count(c => c == '\n') != 1)
            return new(InstallationLockStatus.Unknown);
        var value = text[5..^1];
        if (exitCode == 0)
            return Success(action, value);
        if (exitCode == 3)
            return Refusal(value);
        return new(InstallationLockStatus.Unknown);
    }

    private static InstallationLockResult Success(InstallationLockAction action, string value) => (action, value) switch
    {
        (InstallationLockAction.Acquire, "acquired") => new(InstallationLockStatus.Acquired),
        (InstallationLockAction.Acquire, "reacquired") => new(InstallationLockStatus.Reacquired),
        (InstallationLockAction.Release, "released") => new(InstallationLockStatus.Released),
        (InstallationLockAction.Release, "not-held") => new(InstallationLockStatus.NotHeld),
        _ => new(InstallationLockStatus.Unknown),
    };

    private static InstallationLockResult Refusal(string value)
    {
        if (value == "not-owned")
            return new(InstallationLockStatus.NotOwned);
        if (value == "invalid")
            return new(InstallationLockStatus.Invalid);
        var parts = value.Split(':');
        if (parts.Length != 3 || parts[0] != "held")
            return new(InstallationLockStatus.Unknown);
        Guid? holder = Guid.TryParseExact(parts[1], "D", out var parsed) ? parsed : null;
        if (holder is null && parts[1] != "unknown")
            return new(InstallationLockStatus.Unknown);
        long? age = long.TryParse(parts[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var seconds) ? seconds : null;
        if (age is null && parts[2] != "unknown")
            return new(InstallationLockStatus.Unknown);
        return new(InstallationLockStatus.HeldByOther, holder, age);
    }
}

/// <summary>Installation lock below this installation's own marker, over a fresh pinned SSH connection
/// per call. Same-session reacquire is allowed (the local journal lease already makes that session
/// exclusive on its computer); another session's lock is reported, never taken over or removed.</summary>
public sealed class NasInstallationLock
{
    private readonly SshConnectionTarget target;
    private readonly Func<TimeSpan, INasPathWire> createWire;
    private readonly TimeSpan timeout;

    public NasInstallationLock(SshConnectionTarget target, SshProbeCredentials credentials, TimeSpan? timeout = null)
        : this(target, budget => new SshNetProbeWire(target, credentials, budget), timeout ?? TimeSpan.FromSeconds(20))
    {
        ArgumentNullException.ThrowIfNull(credentials);
    }

    internal NasInstallationLock(SshConnectionTarget target, Func<TimeSpan, INasPathWire> createWire, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(2))
            throw new ArgumentException("lock.timeout.invalid");
        this.target = target;
        this.createWire = createWire;
        this.timeout = timeout;
    }

    public Task<InstallationLockResult> AcquireAsync(MediaPathLayout layout, InstallationOwner owner, Guid session, SshHostKey approvedKey, CancellationToken token) =>
        RunAsync(InstallationLockAction.Acquire, layout, owner, session, approvedKey, token);

    public Task<InstallationLockResult> ReleaseAsync(MediaPathLayout layout, InstallationOwner owner, Guid session, SshHostKey approvedKey, CancellationToken token) =>
        RunAsync(InstallationLockAction.Release, layout, owner, session, approvedKey, token);

    private async Task<InstallationLockResult> RunAsync(InstallationLockAction action, MediaPathLayout layout, InstallationOwner owner,
        Guid session, SshHostKey approvedKey, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(approvedKey);
        var pinValid = approvedKey.IsValid && approvedKey.EndpointFingerprint == target.EndpointFingerprint;
        if (!owner.IsValid || session == Guid.Empty || !pinValid)
            return new(InstallationLockStatus.NotSent);
        var marker = new OwnershipMarker(OwnershipMarker.CurrentSchema, owner.InstallationId, owner.ProfileFingerprint);
        var request = NasLockProtocol.Request(action, layout, marker, session);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        return await Task.Run(() => RunCoreAsync(action, request, approvedKey, deadline.Token), CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<InstallationLockResult> RunCoreAsync(InstallationLockAction action, string request, SshHostKey approvedKey, CancellationToken budget)
    {
        var trust = new SshHostKeyCheck(target.EndpointFingerprint, approvedKey);
        var sent = false;
        try
        {
            budget.ThrowIfCancellationRequested();
            using var wire = createWire(timeout);
            await wire.ConnectAsync(trust.Accept, budget).ConfigureAwait(false);
            if (!trust.Accepted || trust.Failure is not null)
                return new(InstallationLockStatus.NotSent);
            budget.ThrowIfCancellationRequested();
            var (exitCode, output) = await wire.RunLockAsync(request, () => sent = true, budget).ConfigureAwait(false);
            return NasLockProtocol.Parse(action, exitCode, output);
        }
        catch (Exception)
        {
            return new(sent ? InstallationLockStatus.Unknown : InstallationLockStatus.NotSent);
        }
    }
}

/// <summary>Adds the remote installation lock to a journal lease: the local lease first, then the NAS
/// lock, held for the whole Observe/Apply/Verify/confirm and released before the local lease. Any
/// result other than holding the lock is a lease conflict for the scenario, so no step runs.</summary>
public sealed class RemoteLockedOperationJournal(IOperationJournal inner, NasInstallationLock remote, MediaPathLayout layout,
    InstallationOwner owner, Guid session, SshHostKey pin) : IOperationJournal
{
    public InstallationLockResult? LastAcquire { get; private set; }

    public InstallationLockResult? LastRelease { get; private set; }

    public async Task<IAsyncDisposable> AcquireAsync(OperationDescriptor descriptor, CancellationToken token)
    {
        var local = await inner.AcquireAsync(descriptor, token).ConfigureAwait(false);
        try
        {
            LastAcquire = await remote.AcquireAsync(layout, owner, session, pin, token).ConfigureAwait(false);
            if (!LastAcquire.IsHeldBySession)
                throw new OperationLeaseUnavailableException();
            return new Lease(this, local);
        }
        catch
        {
            await local.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task ReleaseRemoteAsync() =>
        LastRelease = await remote.ReleaseAsync(layout, owner, session, pin, CancellationToken.None).ConfigureAwait(false);

    public Task<OperationJournalEntry?> ReadAsync(OperationDescriptor descriptor, CancellationToken token) => inner.ReadAsync(descriptor, token);

    public Task RecordIntentAsync(OperationIntent intent, CancellationToken token) => inner.RecordIntentAsync(intent, token);

    public Task ConfirmAsync(OperationIntent intent, OperationVerification verification, CancellationToken token) =>
        inner.ConfirmAsync(intent, verification, token);

    /// <summary>A failed release leaves this session's own lock; the same session reacquires it later,
    /// another session sees it held. Release never runs on a caller-cancelled token.</summary>
    private sealed class Lease(RemoteLockedOperationJournal owner, IAsyncDisposable local) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await owner.ReleaseRemoteAsync().ConfigureAwait(false);
            }
            finally
            {
                await local.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
