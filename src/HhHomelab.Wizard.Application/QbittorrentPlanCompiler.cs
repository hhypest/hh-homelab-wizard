using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.Application;

public interface IQbittorrentPlanProfile
{
    QbittorrentPlanProfile Definition { get; }
}

public sealed record QbittorrentPlanRevalidation(OperationRead<QbittorrentInstallationPlan> Fresh, bool Unchanged);

/// <summary>Read-only compiler. Each call obtains new host and path evidence through the existing
/// ports. There is no write port, execution adapter, credential store or permission to install.</summary>
public sealed class QbittorrentPlanCompiler(IQbittorrentPlanProfile profile, INasPreflightProbe preflight, INasPathProbe paths, TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private int running;

    public async Task<OperationRead<QbittorrentInstallationPlan>> CompileAsync(QbittorrentInstallationRequest request, SshHostKey approvedKey,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            return Failed(OperationOutcomeKind.Conflict, OperationErrorCode.OperationInProgress);
        try
        {
            token.ThrowIfCancellationRequested();
            if (approvedKey is not { IsValid: true } || approvedKey.EndpointFingerprint != request.Target.EndpointFingerprint)
                return Failed(OperationOutcomeKind.Conflict, OperationErrorCode.IdentityMismatch);
            // Capture the build-time definition once; a provider cannot change it halfway through collection.
            var definition = profile.Definition;
            var host = await preflight.CollectAsync(approvedKey, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (host.Snapshot is null)
                return FailureFor(host.Status);
            var principal = ConfigurationRequest.Fingerprint(new { request.Target.EndpointFingerprint, request.Target.Username });
            if (host.Snapshot.HostKey != approvedKey || host.Snapshot.ProfileFingerprint != definition.ResearchFingerprint
                || host.Snapshot.PrincipalFingerprint != principal)
                return Failed(OperationOutcomeKind.Conflict, OperationErrorCode.IdentityMismatch);
            var inspected = await paths.InspectAsync(request.Layout, approvedKey, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (inspected.Snapshot is null)
                return FailureFor(inspected.Status);
            if (!QbittorrentInstallationPlan.EvidenceIsBound(request, definition, host.Snapshot, inspected.Snapshot, approvedKey))
                return Failed(OperationOutcomeKind.Conflict, OperationErrorCode.IdentityMismatch);
            var now = time.GetUtcNow();
            if (!QbittorrentInstallationPlan.EvidenceIsFresh(request, definition, host.Snapshot, inspected.Snapshot, approvedKey, now))
                return Failed(OperationOutcomeKind.Conflict, OperationErrorCode.ObservationChanged);
            return OperationRead<QbittorrentInstallationPlan>.Observed(new(request, definition, host.Snapshot, inspected.Snapshot, approvedKey, now));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return Failed(OperationOutcomeKind.Cancelled, OperationErrorCode.Cancelled);
        }
        catch (ArgumentException)
        {
            return Failed(OperationOutcomeKind.Invalid, OperationErrorCode.InvalidRequest);
        }
        catch (Exception)
        {
            return Failed(OperationOutcomeKind.Transport, OperationErrorCode.ReadFailed);
        }
        finally
        {
            Volatile.Write(ref running, 0);
        }
    }

    public async Task<QbittorrentPlanRevalidation> RevalidateAsync(QbittorrentInstallationPlan previous, SshHostKey approvedKey,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(previous);
        var fresh = await CompileAsync(previous.Request, approvedKey, token).ConfigureAwait(false);
        return new(fresh, fresh.Value is { } plan && plan.Fingerprint == previous.Fingerprint);
    }

    private static OperationRead<QbittorrentInstallationPlan> FailureFor(SshProbeStatus status) => status switch
    {
        SshProbeStatus.HostKeyApprovalRequired or SshProbeStatus.HostKeyChanged => Failed(OperationOutcomeKind.Conflict, OperationErrorCode.IdentityMismatch),
        SshProbeStatus.AuthenticationRequired => Failed(OperationOutcomeKind.Invalid, OperationErrorCode.AuthenticationRequired),
        SshProbeStatus.InvalidInput => Failed(OperationOutcomeKind.Invalid, OperationErrorCode.InvalidRequest),
        SshProbeStatus.InvalidResponse => Failed(OperationOutcomeKind.Invalid, OperationErrorCode.ResponseInvalid),
        SshProbeStatus.Cancelled => Failed(OperationOutcomeKind.Cancelled, OperationErrorCode.Cancelled),
        SshProbeStatus.TimedOut => Failed(OperationOutcomeKind.Transport, OperationErrorCode.DeadlineExceeded),
        SshProbeStatus.Busy => Failed(OperationOutcomeKind.Conflict, OperationErrorCode.OperationInProgress),
        _ => Failed(OperationOutcomeKind.Transport, OperationErrorCode.ReadFailed),
    };

    private static OperationRead<QbittorrentInstallationPlan> Failed(OperationOutcomeKind kind, OperationErrorCode code) =>
        OperationRead<QbittorrentInstallationPlan>.Failed(OperationResult.Failed(kind, code));
}
